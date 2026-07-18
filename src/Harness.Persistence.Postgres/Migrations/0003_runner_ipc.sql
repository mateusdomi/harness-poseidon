CREATE TABLE harness.runner_attempts
(
    attempt_id varchar(100) PRIMARY KEY,
    runner_id varchar(100) NOT NULL,
    last_sequence bigint NOT NULL CHECK (last_sequence > 0),
    heartbeat_count integer NOT NULL DEFAULT 0 CHECK (heartbeat_count >= 0),
    completed boolean NOT NULL DEFAULT false,
    inbox_count integer NOT NULL DEFAULT 0 CHECK (inbox_count >= 0),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    updated_at timestamptz NOT NULL,
    CHECK (length(trim(attempt_id)) > 0),
    CHECK (length(trim(runner_id)) > 0)
);

CREATE TABLE harness.runner_checkpoints
(
    attempt_id varchar(100) NOT NULL REFERENCES harness.runner_attempts(attempt_id),
    sequence bigint NOT NULL CHECK (sequence > 0),
    checkpoint_id varchar(200) NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (attempt_id, sequence),
    UNIQUE (attempt_id, checkpoint_id),
    CHECK (length(trim(checkpoint_id)) > 0)
);

CREATE TABLE harness.runner_inbox_messages
(
    attempt_id varchar(100) NOT NULL REFERENCES harness.runner_attempts(attempt_id),
    idempotency_key varchar(200) NOT NULL,
    message_hash char(64) NOT NULL,
    response_json jsonb NOT NULL,
    processed_at timestamptz NOT NULL,
    PRIMARY KEY (attempt_id, idempotency_key),
    CHECK (length(trim(idempotency_key)) > 0)
);

CREATE TABLE harness.runner_outbox_messages
(
    attempt_id varchar(100) NOT NULL REFERENCES harness.runner_attempts(attempt_id),
    sequence bigint NOT NULL CHECK (sequence > 0),
    event_type varchar(200) NOT NULL,
    payload_json jsonb NOT NULL,
    occurred_at timestamptz NOT NULL,
    dispatched_at timestamptz NULL,
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    PRIMARY KEY (attempt_id, sequence),
    CHECK (length(trim(event_type)) > 0)
);

CREATE INDEX ix_runner_outbox_pending
    ON harness.runner_outbox_messages (occurred_at, attempt_id, sequence)
    WHERE dispatched_at IS NULL;
