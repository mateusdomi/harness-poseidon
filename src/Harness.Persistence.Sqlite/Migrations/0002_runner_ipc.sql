CREATE TABLE runner_attempts
(
    attempt_id TEXT PRIMARY KEY CHECK (length(attempt_id) BETWEEN 1 AND 100),
    runner_id TEXT NOT NULL CHECK (length(runner_id) BETWEEN 1 AND 100),
    last_sequence INTEGER NOT NULL CHECK (last_sequence > 0),
    heartbeat_count INTEGER NOT NULL DEFAULT 0 CHECK (heartbeat_count >= 0),
    completed INTEGER NOT NULL DEFAULT 0 CHECK (completed IN (0, 1)),
    inbox_count INTEGER NOT NULL DEFAULT 0 CHECK (inbox_count >= 0),
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    updated_at TEXT NOT NULL
);

CREATE TABLE runner_checkpoints
(
    attempt_id TEXT NOT NULL REFERENCES runner_attempts(attempt_id),
    sequence INTEGER NOT NULL CHECK (sequence > 0),
    checkpoint_id TEXT NOT NULL CHECK (length(checkpoint_id) BETWEEN 1 AND 200),
    created_at TEXT NOT NULL,
    PRIMARY KEY (attempt_id, sequence),
    UNIQUE (attempt_id, checkpoint_id)
);

CREATE TABLE runner_inbox_messages
(
    attempt_id TEXT NOT NULL REFERENCES runner_attempts(attempt_id),
    idempotency_key TEXT NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 200),
    message_hash TEXT NOT NULL CHECK (length(message_hash) = 64),
    response_json TEXT NOT NULL,
    processed_at TEXT NOT NULL,
    PRIMARY KEY (attempt_id, idempotency_key)
);

CREATE TABLE runner_outbox_messages
(
    attempt_id TEXT NOT NULL REFERENCES runner_attempts(attempt_id),
    sequence INTEGER NOT NULL CHECK (sequence > 0),
    event_type TEXT NOT NULL CHECK (length(event_type) BETWEEN 1 AND 200),
    payload_json TEXT NOT NULL,
    occurred_at TEXT NOT NULL,
    dispatched_at TEXT NULL,
    attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    PRIMARY KEY (attempt_id, sequence)
);

CREATE INDEX ix_runner_outbox_pending
    ON runner_outbox_messages (occurred_at, attempt_id, sequence)
    WHERE dispatched_at IS NULL;
