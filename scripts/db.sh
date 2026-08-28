#!/usr/bin/env bash
#
# Looks at the database without installing anything. psql already lives in the
# Postgres container, so this needs no client on the host and puts nothing on the
# system drive.
#
#   ./scripts/db.sh                 an interactive psql session
#   ./scripts/db.sh payments        recent payments and where they got to
#   ./scripts/db.sh stuck           payments nothing has resolved
#   ./scripts/db.sh outbox          events, and whether they were published
#   ./scripts/db.sh ledger          money movements, and whether they balance
#   ./scripts/db.sh trace <id>      everything about one payment
#   ./scripts/db.sh sql "SELECT 1"  anything else

set -uo pipefail
cd "$(dirname "$0")/.."

if command -v docker >/dev/null 2>&1; then
  DOCKER=docker
elif [ -x "/e/Docker/program/resources/bin/docker.exe" ]; then
  DOCKER="/e/Docker/program/resources/bin/docker.exe"
else
  DOCKER="/c/Program Files/Docker/Docker/resources/bin/docker.exe"
fi

run() { "$DOCKER" compose exec -T postgres psql -U minipsp -d minipsp -c "$1"; }

case "${1:-shell}" in

shell)
  # -it, so this one is a real session rather than a single query.
  exec "$DOCKER" compose exec postgres psql -U minipsp -d minipsp
  ;;

payments)
  run "SELECT id, status, amount_minor, currency, provider,
              reconciliation_attempts AS tries, created_at
       FROM payments ORDER BY created_at DESC LIMIT 20;"
  ;;

stuck)
  # The question worth asking most often: what has nobody resolved. Anything
  # sitting in unknown means an outcome was never learned; anything old and still
  # created or pending means the pipeline stopped moving.
  run "SELECT status, count(*), min(created_at) AS oldest
       FROM payments
       WHERE status IN ('created', 'pending', 'unknown')
       GROUP BY status ORDER BY oldest;"
  ;;

outbox)
  run "SELECT event_type,
              count(*) FILTER (WHERE published_at IS NOT NULL) AS published,
              count(*) FILTER (WHERE published_at IS NULL AND dead_at IS NULL) AS waiting,
              count(*) FILTER (WHERE dead_at IS NOT NULL) AS dead
       FROM outbox GROUP BY event_type ORDER BY event_type;"

  # Lag is the number that matters: events written but not yet gone out.
  run "SELECT COALESCE(max(now() - created_at)::text, 'nothing waiting') AS oldest_unpublished
       FROM outbox WHERE published_at IS NULL AND dead_at IS NULL;"
  ;;

ledger)
  run "SELECT account_id, sum(amount_minor) AS balance_minor, currency
       FROM ledger_entries GROUP BY account_id, currency ORDER BY account_id;"

  # The invariant. Anything other than zero here means money was created.
  run "SELECT count(*) AS unbalanced_transactions FROM (
         SELECT transaction_id FROM ledger_entries
         GROUP BY transaction_id HAVING sum(amount_minor) <> 0) x;"
  ;;

trace)
  [ $# -ge 2 ] || { echo "usage: db.sh trace <payment-id>" >&2; exit 1; }

  run "SELECT id, status, amount_minor, currency, provider, provider_payment_id,
              version, reconciliation_attempts, created_at, updated_at
       FROM payments WHERE id = '$2';"

  run "SELECT id, event_type, correlation_id,
              published_at IS NOT NULL AS published, attempts, dead_at
       FROM outbox WHERE aggregate_id = '$2' ORDER BY id;"

  run "SELECT e.account_id, e.amount_minor, e.currency, t.kind
       FROM ledger_entries e JOIN ledger_transactions t ON t.id = e.transaction_id
       WHERE t.payment_id = '$2' ORDER BY e.amount_minor DESC;"
  ;;

sql)
  [ $# -ge 2 ] || { echo "usage: db.sh sql \"SELECT ...\"" >&2; exit 1; }
  run "$2"
  ;;

*)
  sed -n '3,14p' "$0" | sed 's/^# \{0,1\}//'
  ;;
esac
