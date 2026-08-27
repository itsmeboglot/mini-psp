-- mini-psp initial schema
--
-- Conventions:
--   * Money is an integer count of minor units (cents, kopiyky) plus an ISO 4217
--     code. Never a floating point type. See docs/adr/0002-money-as-minor-units.md
--   * Status is text + CHECK rather than a PostgreSQL ENUM: adding a value to an
--     ENUM needs ALTER TYPE, which is awkward to roll out. Storage cost is
--     negligible at this size.
--   * Every table carries created_at; mutable rows also carry updated_at.

BEGIN;

CREATE TABLE payments (
    id                  uuid        PRIMARY KEY,
    merchant_id         uuid        NOT NULL,
    status              text        NOT NULL,
    amount_minor        bigint      NOT NULL,
    currency            char(3)     NOT NULL,
    provider            text        NULL,
    provider_payment_id text        NULL,
    -- Optimistic concurrency: every state transition bumps this and asserts the
    -- expected value, so two concurrent writers cannot both win.
    version             integer     NOT NULL DEFAULT 1,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT payments_status_allowed CHECK (status IN (
        'created', 'pending', 'authorized', 'captured',
        'failed', 'expired', 'unknown', 'refunded'
    )),
    CONSTRAINT payments_amount_positive CHECK (amount_minor > 0),
    CONSTRAINT payments_currency_upper  CHECK (currency = upper(currency))
);

-- Supports the worker's queue scan: "oldest payments still in state X".
CREATE INDEX payments_status_created_idx ON payments (status, created_at);

-- Idempotency lives in PostgreSQL, not Redis. The primary key IS the
-- correctness guarantee: two concurrent requests carrying the same key race,
-- one inserts, the other gets a unique violation and returns the stored
-- response. See docs/adr/0003-idempotency-in-postgres.md
CREATE TABLE idempotency_keys (
    merchant_id     uuid        NOT NULL,
    idempotency_key text        NOT NULL,
    -- Hash of the canonical request body. Same key with a different body is a
    -- client bug, not a retry, and must be rejected rather than served the old
    -- response.
    request_hash    text        NOT NULL,
    payment_id      uuid        NOT NULL REFERENCES payments (id),
    response_status smallint    NOT NULL,
    response_body   jsonb       NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),

    PRIMARY KEY (merchant_id, idempotency_key)
);

-- Transactional outbox. The state change and the event it produces are written
-- in ONE transaction; a dispatcher publishes to Kafka afterwards.
-- See docs/adr/0001-transactional-outbox.md
CREATE TABLE outbox (
    id           bigserial   PRIMARY KEY,
    aggregate_id uuid        NOT NULL,
    event_type   text        NOT NULL,
    payload      jsonb       NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    published_at timestamptz NULL
);

-- Partial index: the dispatcher only ever reads unpublished rows, so published
-- ones must not bloat the index it scans.
CREATE INDEX outbox_unpublished_idx
    ON outbox (created_at)
    WHERE published_at IS NULL;

COMMIT;
