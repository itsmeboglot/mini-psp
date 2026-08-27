# 0002. Represent money as integer minor units

Status: accepted

## Context

Amounts cross several boundaries: JSON over HTTP, a PostgreSQL column, C# domain
code, and provider APIs whose own conventions differ. A representation that
loses precision anywhere loses money.

Binary floating point cannot hold most decimal fractions exactly. `0.1 + 0.2`
is not `0.3`. Summing thousands of such values drifts, and a ledger that drifts
cannot be reconciled.

## Decision

An amount is a 64-bit integer count of the currency's minor unit, carried
alongside an ISO 4217 code. `12.34 USD` is `{ "amount_minor": 1234, "currency":
"USD" }`.

* PostgreSQL: `bigint` plus `char(3)`.
* C#: a `Money` value type wrapping `long` and a currency code, constructed
  only through validation. No implicit conversion from `double` exists. When
  arithmetic is added it will be defined on the type, so mixing currencies fails
  to compile rather than at runtime.
* Transport: integers in JSON. Never a decimal string, never a float.

Rounding is never implicit. Any operation that cannot produce an exact minor-unit
result (percentage fees, FX) takes an explicit rounding mode at the call site.

## Consequences

* Exact arithmetic and exact equality. A ledger can assert that entries sum to
  zero and mean it.
* `long` reaches ~92 quadrillion minor units, far beyond any realistic amount.
* Minor-unit exponent is currency-specific: USD and EUR use 2, JPY uses 0, KWD
  uses 3. Formatting for display must consult the currency, so the exponent
  belongs in a currency table rather than a hardcoded division by 100.
* Provider adapters must convert at the edge. Some accept decimal strings, and
  crypto uses far more decimals than fiat, so each connector owns that mapping
  and is tested on it.

## Alternatives rejected

* **`decimal` / `NUMERIC`.** Exact, and a defensible choice. Rejected because it
  still permits a fractional cent to exist and be silently rounded later;
  integers make an invalid amount unrepresentable rather than merely discouraged.
* **`double`.** Not viable for money.
