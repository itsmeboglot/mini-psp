-- Consumer side deduplication.
--
-- At-least-once delivery is not a defect to be engineered away, it is what the
-- outbox buys in exchange for atomicity: the dispatcher can publish a record and
-- die before marking it published, and will publish it again on restart. What
-- makes that harmless is consumers recording what they have already handled.
--
-- Keyed by consumer as well as event, because every consumer group sees every
-- event and each has to decide independently whether it has seen this one.
CREATE TABLE processed_events (
    consumer     text        NOT NULL,
    -- The outbox row id, carried on the message as the outbox-id header. Stable
    -- across republishes of the same record, which a Kafka offset is not.
    event_id     bigint      NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT processed_events_pkey PRIMARY KEY (consumer, event_id)
);
