-- Reconciliation bookkeeping.
--
-- A payment reaches unknown when a provider gave no verdict, and until something
-- asks the provider what actually happened it stays there. These columns are what
-- lets that asking be paced, shared between instances, and eventually give up.

ALTER TABLE payments
    -- How many times the provider has been asked about this payment. A provider
    -- that has still never heard of it after several tries did not take the
    -- money, and the payment can finally be called failed.
    ADD COLUMN reconciliation_attempts integer NOT NULL DEFAULT 0,
    -- Doubles as a soft claim: an instance stamps it before calling the provider,
    -- so another instance passes over the row for the length of the retry
    -- interval. A real lock cannot be held here, because the provider call must
    -- happen outside any transaction.
    ADD COLUMN last_reconciled_at timestamptz NULL;

-- Oldest first, never-tried before already-tried.
CREATE INDEX payments_unresolved_idx
    ON payments (last_reconciled_at NULLS FIRST, created_at)
    WHERE status = 'unknown';
