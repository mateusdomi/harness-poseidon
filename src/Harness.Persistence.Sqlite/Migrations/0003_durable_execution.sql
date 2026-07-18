CREATE TABLE durable_executions
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    project_id TEXT NOT NULL REFERENCES projects(id),
    state TEXT NOT NULL CHECK (state IN
        ('ready', 'running', 'paused', 'waiting_retry', 'waiting_signal',
         'completed', 'cancelled', 'dead_letter')),
    payload_json TEXT NOT NULL,
    max_attempts INTEGER NOT NULL CHECK (max_attempts > 0),
    retry_initial_ms INTEGER NOT NULL CHECK (retry_initial_ms >= 0),
    retry_multiplier TEXT NOT NULL CHECK (CAST(retry_multiplier AS REAL) >= 1),
    retry_maximum_ms INTEGER NOT NULL CHECK (retry_maximum_ms >= retry_initial_ms),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    available_at TEXT NOT NULL,
    active_attempt_id TEXT NULL,
    fencing_token INTEGER NOT NULL DEFAULT 0 CHECK (fencing_token >= 0),
    last_error TEXT NULL,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id, id)
);

CREATE TABLE durable_attempts
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    execution_id TEXT NOT NULL REFERENCES durable_executions(id),
    attempt_number INTEGER NOT NULL CHECK (attempt_number > 0),
    state TEXT NOT NULL CHECK (state IN ('running', 'completed', 'failed', 'abandoned', 'cancelled')),
    owner TEXT NOT NULL CHECK (length(owner) BETWEEN 1 AND 200),
    fencing_token INTEGER NOT NULL CHECK (fencing_token > 0),
    lease_expires_at TEXT NOT NULL,
    last_heartbeat_at TEXT NOT NULL,
    started_at TEXT NOT NULL,
    ended_at TEXT NULL,
    error_code TEXT NULL,
    error_detail TEXT NULL,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    UNIQUE (execution_id, attempt_number),
    UNIQUE (execution_id, fencing_token)
);

CREATE TABLE durable_checkpoints
(
    execution_id TEXT NOT NULL REFERENCES durable_executions(id),
    attempt_id TEXT NOT NULL REFERENCES durable_attempts(id),
    checkpoint_key TEXT NOT NULL CHECK (length(checkpoint_key) BETWEEN 1 AND 200),
    payload_json TEXT NOT NULL,
    payload_hash TEXT NOT NULL CHECK (length(payload_hash) = 64),
    created_at TEXT NOT NULL,
    PRIMARY KEY (execution_id, checkpoint_key)
);

CREATE TABLE durable_timers
(
    execution_id TEXT NOT NULL REFERENCES durable_executions(id),
    timer_id TEXT NOT NULL CHECK (length(timer_id) BETWEEN 1 AND 200),
    due_at TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    fired_at TEXT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (execution_id, timer_id)
);

CREATE TABLE durable_signals
(
    execution_id TEXT NOT NULL REFERENCES durable_executions(id),
    idempotency_key TEXT NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 200),
    signal_name TEXT NOT NULL CHECK (length(signal_name) BETWEEN 1 AND 200),
    payload_json TEXT NOT NULL,
    received_at TEXT NOT NULL,
    PRIMARY KEY (execution_id, idempotency_key)
);

CREATE TABLE durable_transitions
(
    execution_id TEXT NOT NULL REFERENCES durable_executions(id),
    sequence INTEGER NOT NULL CHECK (sequence > 0),
    from_state TEXT NULL,
    to_state TEXT NOT NULL,
    reason TEXT NOT NULL CHECK (length(reason) BETWEEN 1 AND 200),
    attempt_id TEXT NULL REFERENCES durable_attempts(id),
    occurred_at TEXT NOT NULL,
    PRIMARY KEY (execution_id, sequence)
);

CREATE TABLE durable_dead_letters
(
    execution_id TEXT PRIMARY KEY REFERENCES durable_executions(id),
    attempt_id TEXT NULL REFERENCES durable_attempts(id),
    error_code TEXT NOT NULL CHECK (length(error_code) BETWEEN 1 AND 200),
    error_detail TEXT NOT NULL,
    dead_lettered_at TEXT NOT NULL,
    resolved_at TEXT NULL
);

CREATE TABLE durable_command_inbox
(
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    idempotency_key TEXT NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 200),
    command_hash TEXT NOT NULL CHECK (length(command_hash) = 64),
    response_json TEXT NOT NULL,
    processed_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, idempotency_key)
);

CREATE TABLE durable_execution_outbox
(
    execution_id TEXT NOT NULL REFERENCES durable_executions(id),
    transition_sequence INTEGER NOT NULL CHECK (transition_sequence > 0),
    event_type TEXT NOT NULL CHECK (length(event_type) BETWEEN 1 AND 200),
    payload_json TEXT NOT NULL,
    occurred_at TEXT NOT NULL,
    dispatched_at TEXT NULL,
    attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    PRIMARY KEY (execution_id, transition_sequence)
);

CREATE INDEX ix_durable_executions_acquire
    ON durable_executions (tenant_id, available_at, id)
    WHERE state = 'ready';
CREATE INDEX ix_durable_attempts_expired
    ON durable_attempts (lease_expires_at, execution_id)
    WHERE state = 'running';
CREATE INDEX ix_durable_timers_due
    ON durable_timers (due_at, execution_id)
    WHERE fired_at IS NULL;
CREATE INDEX ix_durable_outbox_pending
    ON durable_execution_outbox (occurred_at, execution_id, transition_sequence)
    WHERE dispatched_at IS NULL;
