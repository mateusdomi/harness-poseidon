CREATE TABLE harness.durable_executions
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    project_id char(26) NOT NULL REFERENCES harness.projects(id),
    state varchar(32) NOT NULL CHECK (state IN
        ('ready', 'running', 'paused', 'waiting_retry', 'waiting_signal',
         'completed', 'cancelled', 'dead_letter')),
    payload_json jsonb NOT NULL,
    max_attempts integer NOT NULL CHECK (max_attempts > 0),
    retry_initial_ms bigint NOT NULL CHECK (retry_initial_ms >= 0),
    retry_multiplier numeric(18, 6) NOT NULL CHECK (retry_multiplier >= 1),
    retry_maximum_ms bigint NOT NULL CHECK (retry_maximum_ms >= retry_initial_ms),
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    available_at timestamptz NOT NULL,
    active_attempt_id char(26) NULL,
    fencing_token bigint NOT NULL DEFAULT 0 CHECK (fencing_token >= 0),
    last_error text NULL,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (tenant_id, id)
);

CREATE TABLE harness.durable_attempts
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    execution_id char(26) NOT NULL REFERENCES harness.durable_executions(id),
    attempt_number integer NOT NULL CHECK (attempt_number > 0),
    state varchar(32) NOT NULL CHECK (state IN ('running', 'completed', 'failed', 'abandoned', 'cancelled')),
    owner varchar(200) NOT NULL,
    fencing_token bigint NOT NULL CHECK (fencing_token > 0),
    lease_expires_at timestamptz NOT NULL,
    last_heartbeat_at timestamptz NOT NULL,
    started_at timestamptz NOT NULL,
    ended_at timestamptz NULL,
    error_code varchar(200) NULL,
    error_detail text NULL,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    UNIQUE (execution_id, attempt_number),
    UNIQUE (execution_id, fencing_token),
    CHECK (length(trim(owner)) > 0)
);

CREATE TABLE harness.durable_checkpoints
(
    execution_id char(26) NOT NULL REFERENCES harness.durable_executions(id),
    attempt_id char(26) NOT NULL REFERENCES harness.durable_attempts(id),
    checkpoint_key varchar(200) NOT NULL,
    payload_json jsonb NOT NULL,
    payload_hash char(64) NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (execution_id, checkpoint_key),
    CHECK (length(trim(checkpoint_key)) > 0)
);

CREATE TABLE harness.durable_timers
(
    execution_id char(26) NOT NULL REFERENCES harness.durable_executions(id),
    timer_id varchar(200) NOT NULL,
    due_at timestamptz NOT NULL,
    payload_json jsonb NOT NULL,
    fired_at timestamptz NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (execution_id, timer_id),
    CHECK (length(trim(timer_id)) > 0)
);

CREATE TABLE harness.durable_signals
(
    execution_id char(26) NOT NULL REFERENCES harness.durable_executions(id),
    idempotency_key varchar(200) NOT NULL,
    signal_name varchar(200) NOT NULL,
    payload_json jsonb NOT NULL,
    received_at timestamptz NOT NULL,
    PRIMARY KEY (execution_id, idempotency_key),
    CHECK (length(trim(idempotency_key)) > 0),
    CHECK (length(trim(signal_name)) > 0)
);

CREATE TABLE harness.durable_transitions
(
    execution_id char(26) NOT NULL REFERENCES harness.durable_executions(id),
    sequence bigint NOT NULL CHECK (sequence > 0),
    from_state varchar(32) NULL,
    to_state varchar(32) NOT NULL,
    reason varchar(200) NOT NULL,
    attempt_id char(26) NULL REFERENCES harness.durable_attempts(id),
    occurred_at timestamptz NOT NULL,
    PRIMARY KEY (execution_id, sequence),
    CHECK (length(trim(reason)) > 0)
);

CREATE TABLE harness.durable_dead_letters
(
    execution_id char(26) PRIMARY KEY REFERENCES harness.durable_executions(id),
    attempt_id char(26) NULL REFERENCES harness.durable_attempts(id),
    error_code varchar(200) NOT NULL,
    error_detail text NOT NULL,
    dead_lettered_at timestamptz NOT NULL,
    resolved_at timestamptz NULL,
    CHECK (length(trim(error_code)) > 0)
);

CREATE TABLE harness.durable_command_inbox
(
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    idempotency_key varchar(200) NOT NULL,
    command_hash char(64) NOT NULL,
    response_json jsonb NOT NULL,
    processed_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, idempotency_key),
    CHECK (length(trim(idempotency_key)) > 0)
);

CREATE TABLE harness.durable_execution_outbox
(
    execution_id char(26) NOT NULL REFERENCES harness.durable_executions(id),
    transition_sequence bigint NOT NULL CHECK (transition_sequence > 0),
    event_type varchar(200) NOT NULL,
    payload_json jsonb NOT NULL,
    occurred_at timestamptz NOT NULL,
    dispatched_at timestamptz NULL,
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    PRIMARY KEY (execution_id, transition_sequence),
    CHECK (length(trim(event_type)) > 0)
);

CREATE INDEX ix_durable_executions_acquire
    ON harness.durable_executions (tenant_id, available_at, id)
    WHERE state = 'ready';
CREATE INDEX ix_durable_attempts_expired
    ON harness.durable_attempts (lease_expires_at, execution_id)
    WHERE state = 'running';
CREATE INDEX ix_durable_timers_due
    ON harness.durable_timers (due_at, execution_id)
    WHERE fired_at IS NULL;
CREATE INDEX ix_durable_outbox_pending
    ON harness.durable_execution_outbox (occurred_at, execution_id, transition_sequence)
    WHERE dispatched_at IS NULL;
