# 0003. Enforce idempotency in PostgreSQL, use Redis only as a cache

Status: accepted

## Context

A client that does not receive a response cannot know whether its request was
applied, so it retries. For a payment, a retry that creates a second charge is
the worst outcome the system can produce. Clients therefore send an
`Idempotency-Key` header, and the platform must guarantee that a key maps to at
most one payment.

Redis is already present for caching and is fast enough to check on every
request, which makes it a tempting place to hold the key.

## Decision

The guarantee lives in PostgreSQL: `idempotency_keys` has
`PRIMARY KEY (merchant_id, idempotency_key)`, and the key row is inserted in the
same transaction that creates the payment.

Two concurrent requests with the same key race into that insert. One commits;
the other gets a unique violation, reads the stored response, and returns it. The
losing request never creates a second payment.

Worth being precise about how that race actually resolves, because it is easy to
describe wrongly. The loser does not fail immediately: PostgreSQL blocks it on
the winner's transaction, because the winner's index entry is not yet final and
the winner may still roll back. Measured with two sessions, a loser waited 4.1
seconds for a winner holding its transaction for 6. Only when the winner commits
does the loser get 23505, and by then the stored response is visible to it.

That has a consequence for the InFlight case, which reports that a key is held by
a request whose response does not exist yet. Because the loser waits, that state
is close to unreachable in normal operation: by the time the violation surfaces,
the winner has committed. It remains as a guard against a key deleted by
retention between the commit and the read, or a read served by a lagging replica.
It is an edge, not a path.

Redis may cache the stored response to spare the database a read on repeated
retries. It is never consulted to decide whether the payment may be created.

A key replayed with a different request body is rejected (HTTP 422) rather than
served the old response: the bodies are compared through `request_hash`. Same key
plus different intent is a client defect, and silently returning an unrelated
payment would hide it.

## Consequences

* Correctness holds under concurrency, process death, and a cold or missing
  Redis. Losing Redis costs latency, not money.
* Every create pays one indexed insert. That is the price of the guarantee.
* Keys need a retention policy; they are not kept forever.
* Stored responses must be complete enough to replay verbatim, so the response
  body is persisted rather than regenerated.

## Alternatives rejected

* **A distributed lock in Redis (Redlock).** A lock is a liveness optimization,
  not a correctness primitive: a client can lose its lock through expiry, a GC
  pause, or clock drift while still believing it holds it, and Redis failover can
  hand the same lock to two holders. A unique index cannot be talked out of its
  guarantee.
* **A `SETNX` key in Redis as the sole record.** Same failure mode, plus the
  record is lost on eviction or flush, which turns a retry into a second charge.
* **Checking for an existing payment before inserting.** A read-then-write has a
  window between the two statements. Two requests both read "absent" and both
  insert.
