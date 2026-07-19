ALTER TABLE harness.solicitations ADD COLUMN kind varchar(20) NOT NULL DEFAULT 'request'
    CHECK (kind IN ('request', 'intervention'));
ALTER TABLE harness.solicitations ADD COLUMN title varchar(500) NOT NULL DEFAULT '';
ALTER TABLE harness.solicitations ADD COLUMN state varchar(20) NOT NULL DEFAULT 'open'
    CHECK (state IN ('open', 'inAnalysis', 'converted', 'answered', 'closed'));
ALTER TABLE harness.solicitations ADD COLUMN supersedes_id char(26) NULL
    REFERENCES harness.solicitations(id);
ALTER TABLE harness.solicitations ADD COLUMN is_internal boolean NOT NULL DEFAULT false;
UPDATE harness.solicitations SET title = left(content, 200) WHERE title = '';

ALTER TABLE harness.demands ADD COLUMN description varchar(20000) NOT NULL DEFAULT '';
ALTER TABLE harness.demands ADD COLUMN state varchar(20) NOT NULL DEFAULT 'open'
    CHECK (state IN ('open', 'inProgress', 'completed', 'cancelled'));
ALTER TABLE harness.demands ADD COLUMN priority varchar(20) NOT NULL DEFAULT 'medium'
    CHECK (priority IN ('low', 'medium', 'high', 'critical'));
ALTER TABLE harness.demands ADD COLUMN source_solicitation_id char(26) NULL
    REFERENCES harness.solicitations(id);
ALTER TABLE harness.demands ADD COLUMN is_internal boolean NOT NULL DEFAULT false;
UPDATE harness.demands SET source_solicitation_id = solicitation_id;

ALTER TABLE harness.work_tasks ADD COLUMN source_demand_id char(26) NULL
    REFERENCES harness.demands(id);
ALTER TABLE harness.work_tasks ADD COLUMN board_state varchar(30) NOT NULL DEFAULT 'ready'
    CHECK (board_state IN
        ('backlog', 'ready', 'development', 'review', 'corrections', 'testsGates', 'blocked', 'done'));
ALTER TABLE harness.work_tasks ADD COLUMN priority varchar(20) NOT NULL DEFAULT 'medium'
    CHECK (priority IN ('low', 'medium', 'high', 'critical'));
ALTER TABLE harness.work_tasks ADD COLUMN assignee_agent_id varchar(200) NULL;
ALTER TABLE harness.work_tasks ADD COLUMN blocked_reason varchar(10000) NULL;
ALTER TABLE harness.work_tasks ADD COLUMN due_at timestamptz NULL;
UPDATE harness.work_tasks SET source_demand_id = demand_id,
    board_state = CASE state WHEN 'running' THEN 'development'
                             WHEN 'awaiting_review' THEN 'review'
                             WHEN 'completed' THEN 'done' ELSE 'ready' END,
    priority = risk_tier;

ALTER TABLE harness.instruction_versions ADD COLUMN author_kind varchar(20) NOT NULL DEFAULT 'chief'
    CHECK (author_kind IN ('user', 'chief', 'agent', 'system'));
ALTER TABLE harness.instruction_versions ADD COLUMN author_id varchar(200) NULL;

ALTER TABLE harness.work_attempts ADD COLUMN duration_ms bigint NULL
    CHECK (duration_ms IS NULL OR duration_ms >= 0);
ALTER TABLE harness.work_attempts ADD COLUMN cost_usd numeric(18,6) NOT NULL DEFAULT 0
    CHECK (cost_usd >= 0);
ALTER TABLE harness.work_attempts ADD COLUMN tokens_input bigint NOT NULL DEFAULT 0
    CHECK (tokens_input >= 0);
ALTER TABLE harness.work_attempts ADD COLUMN tokens_output bigint NOT NULL DEFAULT 0
    CHECK (tokens_output >= 0);
ALTER TABLE harness.work_attempts ADD COLUMN summary varchar(10000) NULL;
ALTER TABLE harness.work_attempts ADD COLUMN failure_reason varchar(10000) NULL;

CREATE TABLE harness.attempt_events
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    project_id char(26) NOT NULL,
    attempt_id char(26) NOT NULL,
    kind varchar(20) NOT NULL CHECK (kind IN ('log', 'toolCall', 'note', 'diff')),
    content varchar(100000) NOT NULL CHECK (length(content) > 0),
    occurred_at timestamptz NOT NULL,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, project_id, attempt_id)
        REFERENCES harness.work_attempts(tenant_id, project_id, id)
);

CREATE INDEX ix_solicitations_board_project
    ON harness.solicitations (tenant_id, project_id, id) WHERE is_internal = false;
CREATE INDEX ix_demands_board_project
    ON harness.demands (tenant_id, project_id, id) WHERE is_internal = false;
CREATE INDEX ix_work_tasks_board_project
    ON harness.work_tasks (tenant_id, project_id, board_state, id);
CREATE INDEX ix_attempt_events_attempt
    ON harness.attempt_events (tenant_id, attempt_id, occurred_at, id);
