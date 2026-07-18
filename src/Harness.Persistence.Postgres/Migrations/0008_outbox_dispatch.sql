ALTER TABLE harness.outbox_messages
    ADD COLUMN available_at timestamptz NULL,
    ADD COLUMN lock_owner varchar(200) NULL,
    ADD COLUMN lock_token bigint NOT NULL DEFAULT 0 CHECK (lock_token >= 0),
    ADD COLUMN lock_expires_at timestamptz NULL,
    ADD COLUMN last_error varchar(4000) NULL,
    ADD COLUMN dead_lettered_at timestamptz NULL;

ALTER TABLE harness.outbox_messages
    ADD CONSTRAINT ck_outbox_lock_consistency CHECK
    ((lock_owner IS NULL AND lock_expires_at IS NULL) OR
     (lock_owner IS NOT NULL AND length(trim(lock_owner)) > 0 AND lock_expires_at IS NOT NULL)),
    ADD CONSTRAINT ck_outbox_terminal_consistency CHECK
    (NOT (dispatched_at IS NOT NULL AND dead_lettered_at IS NOT NULL));

CREATE TABLE harness.outbox_dispatch_failures
(
    id char(26) PRIMARY KEY,
    message_id char(26) NOT NULL REFERENCES harness.outbox_messages(id),
    attempt integer NOT NULL CHECK (attempt > 0),
    error varchar(4000) NOT NULL CHECK (length(trim(error)) > 0),
    dead_lettered boolean NOT NULL,
    occurred_at timestamptz NOT NULL,
    UNIQUE (message_id, attempt)
);

DROP INDEX harness.ix_outbox_pending;
CREATE INDEX ix_outbox_dispatchable
    ON harness.outbox_messages (COALESCE(available_at, occurred_at), occurred_at, id)
    WHERE dispatched_at IS NULL AND dead_lettered_at IS NULL;
CREATE INDEX ix_outbox_expired_claims
    ON harness.outbox_messages (lock_expires_at, id)
    WHERE dispatched_at IS NULL AND dead_lettered_at IS NULL AND lock_owner IS NOT NULL;
CREATE INDEX ix_outbox_failures_message
    ON harness.outbox_dispatch_failures (message_id, attempt);

CREATE OR REPLACE FUNCTION harness.reject_outbox_failure_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'outbox dispatch failures are append-only' USING ERRCODE = '23000';
END;
$$;

CREATE TRIGGER tr_outbox_dispatch_failures_no_mutation
BEFORE UPDATE OR DELETE ON harness.outbox_dispatch_failures
FOR EACH ROW EXECUTE FUNCTION harness.reject_outbox_failure_mutation();
