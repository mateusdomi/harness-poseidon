-- Migration 0134: card Escalated pode ser SUPERSEDIDO por cards menores (ver SQLite homônima).
ALTER TABLE harness.demands DROP CONSTRAINT IF EXISTS demands_state_check;
ALTER TABLE harness.demands ADD CONSTRAINT demands_state_check
    CHECK (state IN ('open', 'inProgress', 'completed', 'cancelled', 'superseded'));

CREATE TABLE harness.task_supersessions
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    original_task_id char(26) NOT NULL,
    replacement_task_ids_json jsonb NOT NULL CHECK (jsonb_typeof(replacement_task_ids_json) = 'array'),
    reason text NOT NULL CHECK (length(reason) BETWEEN 1 AND 10000),
    actor_kind varchar(50) NOT NULL,
    actor_id varchar(200) NOT NULL,
    demand_superseded boolean NOT NULL DEFAULT false,
    occurred_at timestamptz NOT NULL,
    UNIQUE (tenant_id, original_task_id)
);
CREATE INDEX ix_task_supersessions_project
    ON harness.task_supersessions (tenant_id, project_id, occurred_at);
ALTER TABLE harness.task_supersessions ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.task_supersessions FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON harness.task_supersessions
    USING (tenant_id = current_setting('app.tenant_id', true));
