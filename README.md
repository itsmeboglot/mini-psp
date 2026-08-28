# mini-psp

A minimal payment service provider core: it accepts payment requests, drives each
one through an explicit state machine, talks to unreliable payment providers, and
keeps a ledger that balances.

The interesting problems here are not CRUD. A provider answers with a timeout and
you do not know whether the customer was charged. A webhook arrives twice, or
arrives before the response to the request that caused it. A client retries a
request it never got an answer to. Money must not be created or destroyed by any
of it.

This README describes what exists. Anything still to come is under
[Planned](#planned), kept separate on purpose so that the two are never confused.

## What runs today

```
  POST /v1/payments                      rate limited per merchant (Redis, Lua)
  (Idempotency-Key)                      replayed answers cached (Redis)
         |
         v
  +---------------+   one transaction: payment + idempotency key + event
  |  Payments.Api |-------------------------------+
  +---------------+                               |
         | dispatcher                             v
         |                              +---------------------+
         +----------------------------->|     PostgreSQL      |
                    Kafka               |  payments           |
                      |                 |  idempotency_keys   |
                      v                 |  outbox             |
              +----------------+        |  processed_events   |
              | Payments.Worker|<------>|  ledger_*           |
              +----------------+        +---------------------+
                | charge          reconciliation sweep
                v
        +-----------------+
        | payment provider|   times out, 500s after charging, declines
        +-----------------+
```

A payment is created, handed to a provider, resolved into authorized, failed or
unknown, and posted to a double entry ledger. Whatever the provider never
answered is chased down afterwards by reconciliation.

| Method | Route | Behaviour |
| --- | --- | --- |
| `POST` | `/v1/payments` | Creates a payment. Requires `Idempotency-Key`. Replays the stored response for a repeated key, `409` while a duplicate is in flight, `422` for a key reused with a different body, `429` past the merchant's rate limit. |
| `GET` | `/v1/payments/{id}` | Returns a payment, or `404`. |
| `GET` | `/health` | Fails when PostgreSQL is unreachable. |
| `GET` | `/metrics` | Prometheus. The worker serves its own on port 8082. |

## Payment states

```
  created ──> pending ──> authorized ──> captured ──> refunded
     │           │            │
     │           ├──> failed  ├──> failed
     │           ├──> expired │
     │           └──> unknown ┘
     └──> failed
```

`unknown` is the state that matters. It means a provider call did not return a
verdict, so the outcome is genuinely undetermined. It is not terminal and it is
never guessed into `failed`: it is resolved by querying the provider or by
reconciliation against its report, which may legitimately discover that the money
was taken. `failed`, `expired` and `refunded` are terminal.

The transition table lives in `PaymentTransitions`, and `Payment.TransitionTo`
refuses anything absent from it by throwing `InvalidPaymentTransitionException`.
Both are covered by unit tests that need no database. No endpoint moves a payment
between states yet — creation is the only write — so the rules are enforced but
not yet exercised over HTTP.

## Design decisions

Recorded as ADRs, one per decision, in [`docs/adr`](docs/adr):

| ADR | Decision |
| --- | --- |
| [0001](docs/adr/0001-transactional-outbox.md) | Events are published through a transactional outbox, not a dual write |
| [0002](docs/adr/0002-money-as-minor-units.md) | Money is an integer count of minor units, never a floating point type |
| [0003](docs/adr/0003-idempotency-in-postgres.md) | Idempotency is guaranteed by a unique index; Redis is only a cache |
| [0004](docs/adr/0004-sql-migrations-over-ef-core.md) | The schema is versioned as SQL, not generated from an EF Core model |

ADR 0001 and the Redis half of ADR 0003 describe decisions taken for work that is
still ahead; they are recorded now because the schema and the API were shaped
around them.

## Stack

**.NET 9** · ASP.NET Core minimal API · **PostgreSQL 16** · **Dapper** ·
**Kafka** (Redpanda locally) · **Redis** · **OpenTelemetry** · Docker Compose ·
xUnit with **Testcontainers**

## Running it

```bash
docker compose up -d postgres
dotnet run --project src/Payments.Api
```

The API applies any pending migrations at startup, so an existing database is
brought up to date rather than needing to be recreated. Compose also defines
Redis, unused so far.

```bash
dotnet test
```

Integration tests start their own PostgreSQL container, so Docker must be
running; they do not touch the compose environment.

## Status

Built:

- [x] Create and fetch a payment, idempotency enforced by a unique index
- [x] Payment state machine, with illegal transitions refused by the domain
- [x] Transactional outbox, drained to Kafka by a dispatcher that tells an outage
      apart from a message the broker will never accept
- [x] An idempotent consumer with retries and a dead letter topic
- [x] A provider connector that records unknown rather than guessing, behind a
      circuit breaker, against a fake provider that fails on purpose
- [x] Reconciliation that resolves what the provider never answered
- [x] Double entry ledger, balanced by a deferred constraint trigger
- [x] Redis for replayed responses and a per-merchant token bucket in Lua
- [x] Correlation ids that survive the hop through Kafka, JSON logs, metrics
- [x] Schema versioned as SQL migrations, applied at startup under an advisory lock
- [x] 84 tests against real containers

Still open, and deliberately:

- [ ] Refunds. The ledger models them; nothing issues one.
- [ ] Webhooks from a provider that answers asynchronously.
- [ ] Routing between several providers, which is where a PSP's conversion rate
      actually comes from.
- [ ] `Money` has no ISO 4217 list and no minor-unit exponent, so JPY would be
      wrong. It needs both before a second currency is taken seriously.
- [ ] Authentication. `GET /v1/payments/{id}` is unscoped, as recorded in
      docs/CONTEXT.md.

## Acceptance tests

The project is done when these hold, not when the endpoints respond.

| | Holds | |
| --- | --- | --- |
| 1 | **yes** | The same idempotency key twice, including twenty times in parallel, yields one payment and identical responses |
| 2 | **yes** | An illegal state transition is refused by the domain |
| 3 | **yes** | A duplicate provider webhook causes one state change; the second is ignored without error |
| 4 | not yet | A webhook arriving before the response to the request that caused it does not corrupt the state. No webhook endpoint exists yet |
| 5 | **yes** | A provider timeout leaves the payment in `unknown`, reconciliation resolves it, and no second charge is issued |
| 6 | **yes** | Kafka being down for 30 seconds loses no events; the outbox drains on recovery and the consumer absorbs the duplicates |
| 7 | structurally | Killing the worker mid-processing loses nothing and doubles nothing. True by construction — the offset is committed after the work — but not yet demonstrated by a test that kills the consumer between the two |
| 8 | **yes** | Ledger entries for every transaction sum to zero |
