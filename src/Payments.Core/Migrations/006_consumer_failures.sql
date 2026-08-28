-- Retry counts that survive a restart.
--
-- Attempts were counted in a loop variable, so a consumer that restarted gave
-- every message its full allowance again. A message that fails on every delivery
-- and a worker that restarts periodically is a pair that never reaches the dead
-- letter topic, and never stops trying either.
CREATE TABLE consumer_failures (
    consumer   text        NOT NULL,
    -- The outbox id from the message header, the same identity
    -- processed_events uses, so the two can be reasoned about together.
    event_id   bigint      NOT NULL,
    attempts   integer     NOT NULL DEFAULT 0,
    last_error text        NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT consumer_failures_pkey PRIMARY KEY (consumer, event_id)
);
