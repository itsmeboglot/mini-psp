# 0001. Publish events through a transactional outbox

Status: accepted

## Context

A payment state change must be persisted and announced to other services. The
obvious implementation writes the row, then publishes to Kafka:

    UPDATE payments SET status = 'authorized' ...;   -- committed
    producer.Produce("payments.authorized", event);  -- may fail

These are two separate systems with no shared transaction. Either side can fail
independently:

* Publish fails after the commit succeeds. The payment is authorized, but no
  downstream service knows. Reconciliation later finds a state nobody acted on.
* The process dies between the two calls. Same outcome, no error to log.
* Publish is retried blindly and the DB write is rolled back. Now consumers act
  on a state that does not exist.

There is no ordering of these two calls that makes the pair atomic.

## Decision

Write the event into an `outbox` table in the same transaction as the state
change. A separate dispatcher polls unpublished rows, produces them to Kafka,
and marks them published.

    BEGIN;
      UPDATE payments SET status = 'authorized', version = version + 1 ...;
      INSERT INTO outbox (aggregate_id, event_type, payload) VALUES (...);
    COMMIT;

## Consequences

* The commit is the single point of truth. If it succeeds, the event will be
  delivered; if it fails, nothing is announced.
* Delivery becomes at-least-once, not exactly-once. The dispatcher can publish a
  row and die before marking it published, so consumers must be idempotent. This
  is a property of the design, not a defect to be patched.
* Ordering per aggregate is preserved by producing with `aggregate_id` as the
  partition key. Ordering across aggregates is not guaranteed and is not needed.
* Cost: one extra insert per state change, plus a dispatcher to operate and
  monitor. Lag between commit and publish becomes a metric worth alerting on.

## Alternatives rejected

* **Dual write with retries.** Retrying the publish narrows the window but never
  closes it, and a retry loop holding a request open makes latency worse.
* **Change data capture (Debezium on the WAL).** Removes the dispatcher and the
  extra insert, and is the better answer at scale. Rejected here because it adds
  a connector to operate and hides the event contract inside table structure,
  which makes the pattern harder to demonstrate and reason about.
* **Two-phase commit across PostgreSQL and Kafka.** Kafka has no XA participant.
  Even where 2PC is available it trades availability for a guarantee that
  idempotent consumers give us more cheaply.
