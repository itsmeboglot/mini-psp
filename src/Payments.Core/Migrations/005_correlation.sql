-- Carries the correlation id across the gap between processes.
--
-- A payment is handled by the API, the dispatcher and the worker, the last of
-- them possibly minutes later. The id is written here inside the same
-- transaction as the payment, put on the Kafka message by the dispatcher, and
-- reopened as a log scope by the consumer, so one query returns the whole story
-- rather than three that have to be lined up by timestamp.
ALTER TABLE outbox ADD COLUMN correlation_id text NULL;
