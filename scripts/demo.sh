#!/usr/bin/env bash
#
# Drives every behaviour this platform claims, against a real stack, and says
# whether each one held.
#
# Rebuilds first, always. Every end-to-end failure this project has had was an
# image that did not contain the change being tested, and no amount of care
# replaces doing it automatically.
#
#   ./scripts/demo.sh          rebuild, run everything
#   ./scripts/demo.sh --fast   skip the rebuild, for a stack already current

set -uo pipefail
cd "$(dirname "$0")/.."

API=http://localhost:8080
PROVIDER=http://localhost:8081
WORKER=http://localhost:8082

passed=0
failed=0

# Docker is not always on PATH on Windows.
if command -v docker >/dev/null 2>&1; then
  DOCKER=docker
elif [ -x "/e/Docker/program/resources/bin/docker.exe" ]; then
  DOCKER="/e/Docker/program/resources/bin/docker.exe"
elif [ -x "/c/Program Files/Docker/Docker/resources/bin/docker.exe" ]; then
  DOCKER="/c/Program Files/Docker/Docker/resources/bin/docker.exe"
else
  echo "docker not found" >&2
  exit 1
fi

compose() { "$DOCKER" compose "$@"; }
psql() { compose exec -T postgres psql -U minipsp -d minipsp -t -A "$@"; }

say()  { printf '\n\033[1m%s\033[0m\n' "$*"; }
check() {
  local label=$1 expected=$2 actual=$3
  if [ "$expected" = "$actual" ]; then
    printf '  \033[32mPASS\033[0m  %-52s %s\n' "$label" "$actual"
    passed=$((passed + 1))
  else
    printf '  \033[31mFAIL\033[0m  %-52s expected %s, got %s\n' "$label" "$expected" "$actual"
    failed=$((failed + 1))
  fi
}

pay() { # amount currency idempotency-key correlation-id -> body
  curl -s -X POST "$API/v1/payments" \
    -H "Content-Type: application/json" \
    -H "Idempotency-Key: $3" \
    -H "X-Correlation-Id: ${4:-demo}" \
    -d "{\"merchantId\":\"$MERCHANT\",\"amountMinor\":$1,\"currency\":\"$2\"}"
}

status_of() { psql -c "SELECT status FROM payments WHERE id='$1';"; }

wait_for_status() { # id expected timeout-seconds
  local waited=0
  while [ "$waited" -lt "$3" ]; do
    [ "$(status_of "$1")" = "$2" ] && return 0
    sleep 2; waited=$((waited + 2))
  done
  return 1
}

# ---------------------------------------------------------------- bring it up

if [ "${1:-}" != "--fast" ]; then
  say "Building images"
  compose build api worker fake-provider >/dev/null || exit 1
fi

say "Starting a clean stack"
compose down -v >/dev/null 2>&1
compose up -d --wait >/dev/null 2>&1 || { compose ps; exit 1; }
sleep 3

MERCHANT=$(cat /proc/sys/kernel/random/uuid 2>/dev/null || echo "11111111-2222-3333-4444-555555555555")

# --------------------------------------------------------------- the scenarios

say "1. Idempotency: the same key twice yields one payment"
first=$(pay 3000 USD idem-key)
replay=$(pay 3000 USD idem-key)
check "one payment in the database" "1" "$(psql -c "SELECT count(*) FROM payments WHERE merchant_id='$MERCHANT';")"
check "the replay is byte identical" "same" "$([ "$first" = "$replay" ] && echo same || echo different)"
check "same key, different body is refused" "422" \
  "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/v1/payments" \
      -H 'Content-Type: application/json' -H 'Idempotency-Key: idem-key' \
      -d "{\"merchantId\":\"$MERCHANT\",\"amountMinor\":9999,\"currency\":\"USD\"}")"

say "2. The provider decides, and we never guess"
auth=$(pay 4000 USD prov-auth | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')
decl=$(pay 4001 USD prov-decl | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')
hang=$(pay 4002 USD prov-hang | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')
oops=$(pay 4003 USD prov-500  | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')

wait_for_status "$auth" authorized 30
check "authorised"                    "authorized" "$(status_of "$auth")"
check "declined becomes failed"       "failed"     "$(status_of "$decl")"
wait_for_status "$hang" unknown 40
check "a hang becomes unknown"        "unknown"    "$(status_of "$hang")"
check "a 500 after charging: unknown" "unknown"    "$(status_of "$oops")"

say "3. The two unknowns are opposites, which is why guessing is wrong"
printf '  the one that hung        provider says: %s\n' \
  "$(curl -s -o /dev/null -w '%{http_code}' "$PROVIDER/charges/$hang") (404 means never charged)"
printf '  the one that 500d        provider says: %s\n' \
  "$(curl -s "$PROVIDER/charges/$oops" | head -c 60)"

say "4. Reconciliation resolves what the provider never answered"
if wait_for_status "$oops" authorized 90; then
  check "the charged one is corrected to authorized" "authorized" "$(status_of "$oops")"
else
  check "the charged one is corrected to authorized" "authorized" "$(status_of "$oops")"
fi
check "attempts were recorded" "yes" \
  "$([ "$(psql -c "SELECT reconciliation_attempts FROM payments WHERE id='$oops';")" -ge 1 ] && echo yes || echo no)"

say "5. The ledger balances"
psql -c "SELECT '  ' || e.account_id || '  ' || e.amount_minor
         FROM ledger_entries e JOIN ledger_transactions t ON t.id = e.transaction_id
         WHERE t.payment_id='$auth';"
check "every transaction sums to zero" "0" \
  "$(psql -c "SELECT count(*) FROM (SELECT transaction_id FROM ledger_entries
              GROUP BY transaction_id HAVING sum(amount_minor) <> 0) x;")"

say "6. One correlation id covers the whole journey"
traced=$(pay 6000 USD corr-key trace-demo | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')
wait_for_status "$traced" authorized 30
check "stamped on every event" "3" \
  "$(psql -c "SELECT count(*) FROM outbox WHERE aggregate_id='$traced' AND correlation_id='trace-demo';")"
printf '  log lines under trace-demo: %s\n' "$(compose logs api worker 2>&1 | grep -c trace-demo)"

say "7. A merchant past its burst is refused"
LIMITED=$(cat /proc/sys/kernel/random/uuid 2>/dev/null || echo "99999999-9999-9999-9999-999999999999")
# Concurrently, and it has to be. Sequential requests take about as long as the
# bucket takes to refill, so they never gain on it: ten tokens a second spent at
# roughly ten a second is a bucket that stays full. A burst is what a limiter
# exists for, so a burst is what tests it.
codes=$(mktemp)
for i in $(seq 1 90); do
  ( curl -s -o /dev/null -w '%{http_code}\n' -X POST "$API/v1/payments" \
      -H 'Content-Type: application/json' -H "Idempotency-Key: rl-$i" \
      -d "{\"merchantId\":\"$LIMITED\",\"amountMinor\":100,\"currency\":\"USD\"}" >> "$codes" ) &
done
wait

allowed=$(grep -c 201 "$codes" || true)
refused=$(grep -c 429 "$codes" || true)
printf '  %s answered: %s allowed, %s refused\n' "$(wc -l < "$codes")" "$allowed" "$refused"

check "a burst past the bucket is refused" "yes" \
  "$([ "$refused" -gt 0 ] && echo yes || echo no)"
rm -f "$codes"

say "8. Health means something"
check "healthy while PostgreSQL is up" "200" "$(curl -s -o /dev/null -w '%{http_code}' "$API/health")"
compose stop postgres >/dev/null 2>&1
check "unhealthy when it is not" "503" "$(curl -s -o /dev/null -w '%{http_code}' "$API/health")"
compose start postgres >/dev/null 2>&1
sleep 6

say "9. Both processes report"
check "api metrics"    "yes" "$(curl -s "$API/metrics"    | grep -q payments_created  && echo yes || echo no)"
check "worker metrics" "yes" "$(curl -s "$WORKER/metrics" | grep -q payments_resolved && echo yes || echo no)"

# -------------------------------------------------------------------- verdict

say "$passed passed, $failed failed"
[ "$failed" -eq 0 ] || exit 1
