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
            POST /v1/payments
        (Idempotency-Key header)
                   |
                   v
        +----------------------+
        |    Payments.Api      |   minimal API, OpenAPI
        +----------------------+
                   |
                   |  one transaction:
                   |    payment row + idempotency row
                   v
        +----------------------+
        |      PostgreSQL      |
        |  payments            |
        |  idempotency_keys    |
        |  outbox (unused yet) |
        +----------------------+
```

Two endpoints:

| Method | Route | Behaviour |
| --- | --- | --- |
| `POST` | `/v1/payments` | Creates a payment. Requires `Idempotency-Key`. Replays the stored response for a repeated key, `409` while a duplicate is still in flight, `422` if the key is reused with a different body. |
| `GET` | `/v1/payments/{id}` | Returns a payment, or `404`. |

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

ADR 0001 and the Redis half of ADR 0003 describe decisions taken for work that is
still ahead; they are recorded now because the schema and the API were shaped
around them.

## Stack

In use: **.NET 9** · ASP.NET Core minimal API · **PostgreSQL 16** · **Dapper** ·
Docker Compose · xUnit with **Testcontainers**

Planned: Kafka, Redis, EF Core for migrations, OpenTelemetry. None of these are
referenced by the code yet.

## Running it

```bash
docker compose up -d postgres
dotnet run --project src/Payments.Api
```

The schema in `db/` is applied by the Postgres entrypoint on first start. Compose
also defines Redis, unused so far, and an `api` service that builds the
Dockerfile.

```bash
dotnet test
```

Integration tests start their own PostgreSQL container, so Docker must be
running; they do not touch the compose environment.

## Status

Built:

- [x] Schema: `payments`, `idempotency_keys`, `outbox`
- [x] Compose environment
- [x] Create and fetch a payment, with idempotency enforced by a unique index
- [x] Payment state machine and its transition rules
- [x] 30 tests: integration against a real PostgreSQL, unit for the domain

Next:

- [ ] Write to `outbox` on every state change, and a dispatcher that drains it to Kafka
- [ ] `Payments.Worker` as an idempotent consumer, with retries and a DLQ
- [ ] Two provider connectors that fail on purpose: timeouts, duplicate
      callbacks, callbacks that arrive early
- [ ] Double-entry ledger, with the invariant that entries sum to zero
- [ ] Reconciliation of `unknown` payments
- [ ] Redis for replayed responses and per-merchant rate limiting
- [ ] Structured logging, correlation ids, metrics

## Acceptance tests

The project is done when these hold, not when the endpoints respond.

| | Holds | |
| --- | --- | --- |
| 1 | **yes** | The same idempotency key twice, including twenty times in parallel, yields one payment and identical responses |
| 2 | **yes** | An illegal state transition is refused by the domain |
| 3 | not yet | A duplicate provider webhook causes one state change; the second is ignored without error |
| 4 | not yet | A webhook arriving before the response to the request that caused it does not corrupt the state |
| 5 | not yet | A provider timeout leaves the payment in `unknown`, reconciliation resolves it, and no second charge is issued |
| 6 | not yet | Kafka being down for 30 seconds loses no events; the outbox drains on recovery and the consumer absorbs the duplicates |
| 7 | not yet | Killing the worker mid-processing loses nothing and doubles nothing |
| 8 | not yet | Ledger entries for every transaction sum to zero |
