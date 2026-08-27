# Context and scope

## What this is

A working core of a payment service provider, built to be read: every mechanism
that makes payments hard is present in a form small enough to follow end to end,
and every non-obvious choice is written down in `docs/adr`.

It is not a product. There is no merchant onboarding, no dashboard, no card data,
no real provider.

## Target stack

Chosen to match a production PSP rather than for convenience:

| Concern | Choice |
| --- | --- |
| Runtime | .NET 9 |
| HTTP | ASP.NET Core minimal API, OpenAPI-first |
| Store | PostgreSQL 16 |
| Data access | Dapper for reads, EF Core for writes and migrations |
| Cache, rate limiting | Redis |
| Messaging | Kafka (Redpanda locally) |
| Packaging | Docker, Compose for the local environment |
| Tests | xUnit, Testcontainers against real services |

Two data access libraries is a deliberate choice, not indecision: writes benefit
from a change-tracked model and generated migrations, reads and reports are
clearer and faster as explicit SQL.

## Deliberately out of scope

* **Card data.** Nothing in this system touches a PAN, which keeps it entirely
  outside PCI DSS scope. Provider connectors exchange tokens.
* **Authentication and merchant management.** A merchant is an id in a request.
  One consequence is worth naming rather than leaving implied: `GET
  /v1/payments/{id}` is not scoped to a merchant, so any caller who knows or
  guesses an id can read any payment. Adding a merchant to the WHERE clause
  without an authenticated caller to compare it against would be theatre, so the
  gap stays until there is an identity to enforce.
* **Real provider integrations.** Two fake connectors stand in, configured to
  fail in the specific ways real ones do: timeouts, 5xx, duplicate callbacks,
  callbacks that arrive before the originating response.
* **Settlement, payouts, fees, FX.** The ledger models money movement for a
  payment and its refund. Merchant settlement is a different problem.
* **Horizontal scale.** Single instance of each service. Where a decision would
  differ at scale, the ADR says so.

## Invariants

These hold at every commit, and the tests exist to prove they hold:

1. One idempotency key maps to at most one payment.
2. A payment's state changes only along legal transitions.
3. No provider is charged twice for one payment.
4. A payment whose outcome is undetermined is `unknown`, never guessed.
5. Ledger entries for a transaction sum to zero.
6. An event is published if and only if the state change that produced it
   committed.

## Conventions

* Money is integer minor units plus an ISO 4217 code. See ADR 0002.
* Every external call carries a timeout, a retry policy, and a correlation id.
* Every log line is structured and carries the payment id.
* Kafka messages are keyed by `payment_id`, so per-payment ordering is
  guaranteed and cross-payment ordering is not relied upon.
* One ADR per decision, written when the decision is made, not afterwards.
