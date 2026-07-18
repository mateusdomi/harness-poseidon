ALTER TABLE outbox_messages ADD COLUMN available_at TEXT NULL;
ALTER TABLE outbox_messages ADD COLUMN lock_owner TEXT NULL
    CHECK (lock_owner IS NULL OR length(lock_owner) BETWEEN 1 AND 200);
ALTER TABLE outbox_messages ADD COLUMN lock_token INTEGER NOT NULL DEFAULT 0
    CHECK (lock_token >= 0);
ALTER TABLE outbox_messages ADD COLUMN lock_expires_at TEXT NULL;
ALTER TABLE outbox_messages ADD COLUMN last_error TEXT NULL
    CHECK (last_error IS NULL OR length(last_error) BETWEEN 1 AND 4000);
ALTER TABLE outbox_messages ADD COLUMN dead_lettered_at TEXT NULL;

CREATE TABLE outbox_dispatch_failures
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    message_id TEXT NOT NULL REFERENCES outbox_messages(id),
    attempt INTEGER NOT NULL CHECK (attempt > 0),
    error TEXT NOT NULL CHECK (length(error) BETWEEN 1 AND 4000),
    dead_lettered INTEGER NOT NULL CHECK (dead_lettered IN (0, 1)),
    occurred_at TEXT NOT NULL,
    UNIQUE (message_id, attempt)
);

DROP INDEX ix_outbox_pending;
CREATE INDEX ix_outbox_dispatchable
    ON outbox_messages (COALESCE(available_at, occurred_at), occurred_at, id)
    WHERE dispatched_at IS NULL AND dead_lettered_at IS NULL;
CREATE INDEX ix_outbox_expired_claims
    ON outbox_messages (lock_expires_at, id)
    WHERE dispatched_at IS NULL AND dead_lettered_at IS NULL AND lock_owner IS NOT NULL;
CREATE INDEX ix_outbox_failures_message
    ON outbox_dispatch_failures (message_id, attempt);

CREATE TRIGGER tr_outbox_dispatch_failures_no_update
BEFORE UPDATE ON outbox_dispatch_failures
BEGIN
    SELECT RAISE(ABORT, 'outbox dispatch failures are append-only');
END;

CREATE TRIGGER tr_outbox_dispatch_failures_no_delete
BEFORE DELETE ON outbox_dispatch_failures
BEGIN
    SELECT RAISE(ABORT, 'outbox dispatch failures are append-only');
END;
