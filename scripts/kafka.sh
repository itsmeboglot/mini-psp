#!/usr/bin/env bash
#
# Looks at the broker. rpk is already inside the Redpanda container, so this
# needs no client on the host.
#
#   ./scripts/kafka.sh topics          what exists, and how much is in it
#   ./scripts/kafka.sh lag             how far behind the consumer is
#   ./scripts/kafka.sh read [n]        the last n messages, readably
#   ./scripts/kafka.sh tail            follow new messages as they arrive
#   ./scripts/kafka.sh dlq             what was set aside, and why
#   ./scripts/kafka.sh trace <id>      every message about one payment
#   ./scripts/kafka.sh rpk <args...>   anything else

set -uo pipefail
cd "$(dirname "$0")/.."

TOPIC=payments.events
DLQ=payments.events.dlq
GROUP=payments-worker

if command -v docker >/dev/null 2>&1; then
  DOCKER=docker
elif [ -x "/e/Docker/program/resources/bin/docker.exe" ]; then
  DOCKER="/e/Docker/program/resources/bin/docker.exe"
else
  DOCKER="/c/Program Files/Docker/Docker/resources/bin/docker.exe"
fi

rpk() { "$DOCKER" compose exec -T redpanda rpk "$@"; }

# rpk prints one JSON object per message. This turns each into a line worth
# reading: which payment, which event, and the correlation id that ties it to the
# request that caused it.
readable() {
  python3 -c '
import json, sys

for raw in sys.stdin.read().split("\n{"):
    text = raw if raw.lstrip().startswith("{") else "{" + raw
    try:
        message = json.loads(text)
    except Exception:
        continue

    headers = {h.get("key"): h.get("value") for h in message.get("headers") or []}
    try:
        body = json.loads(message.get("value") or "{}")
    except Exception:
        body = {}

    print("  offset {:<5} {:<20} payment {} {} {}  corr={}".format(
        message.get("offset", "?"),
        headers.get("event-type", "?"),
        (body.get("paymentId") or message.get("key") or "?")[:8],
        body.get("amountMinor", ""),
        body.get("currency", ""),
        headers.get("correlation-id") or "-"))
'
}

case "${1:-topics}" in

topics)
  rpk topic list
  echo
  # Where each partition currently ends: the total number of events ever
  # published, since nothing is deleted here.
  rpk topic describe "$TOPIC" -p 2>/dev/null | head -10
  ;;

lag)
  # The number that matters in production. Lag rising means the worker is
  # falling behind, and payments are sitting unprocessed.
  rpk group describe "$GROUP"
  ;;

read)
  count=${2:-10}
  rpk topic consume "$TOPIC" --num "$count" --offset start 2>/dev/null | readable
  ;;

tail)
  echo "following $TOPIC, ctrl-c to stop"
  "$DOCKER" compose exec redpanda rpk topic consume "$TOPIC" 2>/dev/null | readable
  ;;

dlq)
  # Empty is the expected state. Anything here failed every retry, and the
  # reason it failed travels with it.
  if rpk topic list 2>/dev/null | grep -q "$DLQ"; then
    rpk topic consume "$DLQ" --num 20 --offset start 2>/dev/null \
      | python3 -c '
import json, sys
found = False
for raw in sys.stdin.read().split("\n{"):
    text = raw if raw.lstrip().startswith("{") else "{" + raw
    try:
        message = json.loads(text)
    except Exception:
        continue
    found = True
    headers = {h.get("key"): h.get("value") for h in message.get("headers") or []}
    print("  {}  {}".format(message.get("key", "?")[:8], headers.get("dead-letter-reason", "no reason given")))
print("  nothing set aside" if not found else "")
'
  else
    echo "  the dead letter topic does not exist yet, which means nothing has needed it"
  fi
  ;;

trace)
  [ $# -ge 2 ] || { echo "usage: kafka.sh trace <payment-id>" >&2; exit 1; }
  rpk topic consume "$TOPIC" --offset start --num 500 2>/dev/null \
    | grep -A0 "$2" >/dev/null 2>&1
  rpk topic consume "$TOPIC" --offset start --num 500 2>/dev/null | readable | grep "${2:0:8}"
  ;;

rpk)
  shift
  rpk "$@"
  ;;

*)
  sed -n '3,12p' "$0" | sed 's/^# \{0,1\}//'
  ;;
esac
