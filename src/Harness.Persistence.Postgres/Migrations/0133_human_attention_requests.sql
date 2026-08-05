-- Migration 0133: SLA de atenção humana (ver SQLite homônima para o racional).
CREATE TABLE harness.human_attention_requests
(
    tenant_id char(26) NOT NULL,
    id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    conversation_id text NULL,
    card_id text NULL,
    graph_node_id text NULL,
    question text NOT NULL,
    reason text NOT NULL,
    classification varchar(10) NOT NULL CHECK (classification IN ('ask')),
    severity varchar(10) NOT NULL CHECK (severity IN ('low', 'medium', 'high', 'critical')),
    blocking_scope text NOT NULL,
    status varchar(15) NOT NULL CHECK (status IN (
        'open', 'notified', 'acknowledged', 'answered', 'expired', 'superseded')),
    source_agent text NOT NULL,
    correlation_id text NOT NULL,
    reminder_count integer NOT NULL DEFAULT 0 CHECK (reminder_count >= 0),
    channel_status text NOT NULL DEFAULT 'none',
    answer text NULL,
    created_at timestamptz NOT NULL,
    acknowledged_at timestamptz NULL,
    answered_at timestamptz NULL,
    next_action_at timestamptz NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, correlation_id)
);
CREATE INDEX ix_human_attention_open
    ON harness.human_attention_requests (tenant_id, status, next_action_at);
CREATE INDEX ix_human_attention_project
    ON harness.human_attention_requests (tenant_id, project_id, status);
ALTER TABLE harness.human_attention_requests ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.human_attention_requests FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON harness.human_attention_requests
    USING (tenant_id = current_setting('app.tenant_id', true));
