# mini-psp

A minimal payment service provider core: it accepts payment requests, drives each
one through an explicit state machine, talks to unreliable payment providers, and
keeps a ledger that balances.

The interesting problems here are not CRUD. A provider answers with a timeout and
you do not know whether the customer was charged. A webhook arrives twice, or
arrives before the response to the request that caused it. A client retries a
request it never got an answer to. Money must not be created or destroyed by any
of it.

## Architecture

```
            POST /v1/payments
        (Idempotency-Key header)
                   |
                   v
        +----------------------+
        |    Payments.Api      |
        +----------------------+
                   |
                   |  one transaction:
                   |    payment row + idempotency row + outbox row
                   v
        +----------------------+        +-----------+
        |      PostgreSQL      |<------>|   Redis   |
        |  payments            |        |  response |
        |  idempotency_keys    |        |  cache,   |
        |  outbox              |        |  rate     |
        |  ledger_entries      |        |  limits   |
        +----------------------+        +-----------+
                   |
                   |  outbox dispatcher
                   v
              +---------+
              |  Kafka  |   partition key = payment_id
              +---------+
                   |
                   v
        +----------------------+        +---------------------+
        |   Payments.Worker    |------->|  provider connector |
        |  idempotent consumer |        |  (A: sync)          |
        |  retries, DLQ        |        |  (B: async webhook) |
        +----------------------+        +---------------------+
                   |
                   |  reconciliation job
                   v
           resolves 'unknown' payments
           against the provider's report
```

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
verdict, so the outcome is genuinely undetermined. A payment in `unknown` is
never guessed into `failed`; it is resolved by querying the provider or by
reconciliation against its daily report. Nothing is retried against a provider
without carrying the original idempotency key.

Illegal transitions are rejected by the domain, not by a check in a controller.

## Design decisions

Recorded as ADRs, one per decision, in [`docs/adr`](docs/adr):

| ADR | Decision |
| --- | --- |
| [0001](docs/adr/0001-transactional-outbox.md) | Events are published through a transactional outbox, not a dual write |
| [0002](docs/adr/0002-money-as-minor-units.md) | Money is an integer count of minor units, never a floating point type |
| [0003](docs/adr/0003-idempotency-in-postgres.md) | Idempotency is guaranteed by a unique index; Redis is only a cache |

## Stack

.NET 9 · ASP.NET Core (minimal API, OpenAPI-first) · PostgreSQL 16 ·
Dapper for reads, EF Core for writes and migrations · Redis · Kafka · Docker

## Running it

```bash
docker compose up -d
```

Brings up PostgreSQL on 5432 and Redis on 6379. The schema in `db/` is applied by
the Postgres entrypoint on first start.

## Status

Built:

- [x] Schema: `payments`, `idempotency_keys`, `outbox`
- [x] Compose environment: PostgreSQL, Redis
- [x] `Payments.Api`: create and fetch a payment, with idempotency enforced
- [x] Integration tests on real containers (Testcontainers)

Next:

- [ ] Outbox dispatcher and Kafka
- [ ] `Payments.Worker` as an idempotent consumer, with a DLQ
- [ ] Two provider connectors that fail on purpose: timeouts, duplicate
      callbacks, callbacks that arrive early
- [ ] Double-entry ledger, with the invariant that entries sum to zero
- [ ] Reconciliation of `unknown` payments

## Acceptance tests

The project is done when these hold, not when the endpoints respond:

1. The same idempotency key twice, including in parallel, yields one payment and
   two identical responses. **Covered.**
2. A duplicate provider webhook causes one state change; the second is ignored
   without error.
3. A webhook that arrives before the response to the request that caused it does
   not corrupt the state.
4. A provider timeout leaves the payment in `unknown`, reconciliation resolves
   it, and no second charge is issued.
5. Kafka being down for 30 seconds loses no events; the outbox drains on
   recovery and the consumer absorbs the duplicates.
6. Killing the worker mid-processing loses nothing and doubles nothing.
7. After any scenario, ledger entries for every transaction sum to zero.
8. An illegal state transition is rejected by the domain.
