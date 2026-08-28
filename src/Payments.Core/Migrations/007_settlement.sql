-- Where our record and the provider's disagree.
--
-- Settlement can discover that a payment we called failed was charged after all.
-- That is not corrected automatically: failed is terminal, the merchant has been
-- told, and a system that quietly rewrites settled history is worse than one that
-- raises its hand. Rows here are for a person.
CREATE TABLE settlement_discrepancies (
    payment_id         uuid        PRIMARY KEY REFERENCES payments (id),
    our_status         text        NOT NULL,
    provider_status    text        NOT NULL,
    provider_reference text        NULL,
    observed_at        timestamptz NOT NULL DEFAULT now(),
    resolved_at        timestamptz NULL
);

-- The only query anyone runs against this: what is still outstanding.
CREATE INDEX settlement_discrepancies_open_idx
    ON settlement_discrepancies (observed_at)
    WHERE resolved_at IS NULL;
