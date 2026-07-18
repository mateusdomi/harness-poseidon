ALTER TABLE solicitations ADD COLUMN kind TEXT NOT NULL DEFAULT 'request'
    CHECK (kind IN ('request','intervention'));
ALTER TABLE solicitations ADD COLUMN title TEXT NOT NULL DEFAULT '';
ALTER TABLE solicitations ADD COLUMN state TEXT NOT NULL DEFAULT 'open'
    CHECK (state IN ('open','inAnalysis','converted','answered','closed'));
ALTER TABLE solicitations ADD COLUMN supersedes_id TEXT NULL REFERENCES solicitations(id);
ALTER TABLE solicitations ADD COLUMN is_internal INTEGER NOT NULL DEFAULT 0
    CHECK (is_internal IN (0,1));
UPDATE solicitations SET title=substr(content,1,200) WHERE title='';

ALTER TABLE demands ADD COLUMN description TEXT NOT NULL DEFAULT '';
ALTER TABLE demands ADD COLUMN state TEXT NOT NULL DEFAULT 'open'
    CHECK (state IN ('open','inProgress','completed','cancelled'));
ALTER TABLE demands ADD COLUMN priority TEXT NOT NULL DEFAULT 'medium'
    CHECK (priority IN ('low','medium','high','critical'));
ALTER TABLE demands ADD COLUMN source_solicitation_id TEXT NULL REFERENCES solicitations(id);
ALTER TABLE demands ADD COLUMN is_internal INTEGER NOT NULL DEFAULT 0
    CHECK (is_internal IN (0,1));
UPDATE demands SET source_solicitation_id=solicitation_id;

ALTER TABLE work_tasks ADD COLUMN source_demand_id TEXT NULL REFERENCES demands(id);
ALTER TABLE work_tasks ADD COLUMN board_state TEXT NOT NULL DEFAULT 'ready'
    CHECK (board_state IN ('backlog','ready','development','review','corrections','testsGates','blocked','done'));
ALTER TABLE work_tasks ADD COLUMN priority TEXT NOT NULL DEFAULT 'medium'
    CHECK (priority IN ('low','medium','high','critical'));
ALTER TABLE work_tasks ADD COLUMN assignee_agent_id TEXT NULL;
ALTER TABLE work_tasks ADD COLUMN blocked_reason TEXT NULL;
ALTER TABLE work_tasks ADD COLUMN due_at TEXT NULL;
UPDATE work_tasks SET source_demand_id=demand_id,
    board_state=CASE state WHEN 'running' THEN 'development'
                           WHEN 'awaiting_review' THEN 'review'
                           WHEN 'completed' THEN 'done' ELSE 'ready' END,
    priority=risk_tier;

ALTER TABLE instruction_versions ADD COLUMN author_kind TEXT NOT NULL DEFAULT 'chief'
    CHECK (author_kind IN ('user','chief','agent','system'));
ALTER TABLE instruction_versions ADD COLUMN author_id TEXT NULL;

ALTER TABLE work_attempts ADD COLUMN duration_ms INTEGER NULL
    CHECK (duration_ms IS NULL OR duration_ms >= 0);
ALTER TABLE work_attempts ADD COLUMN cost_usd REAL NOT NULL DEFAULT 0 CHECK (cost_usd >= 0);
ALTER TABLE work_attempts ADD COLUMN tokens_input INTEGER NOT NULL DEFAULT 0 CHECK (tokens_input >= 0);
ALTER TABLE work_attempts ADD COLUMN tokens_output INTEGER NOT NULL DEFAULT 0 CHECK (tokens_output >= 0);
ALTER TABLE work_attempts ADD COLUMN summary TEXT NULL;
ALTER TABLE work_attempts ADD COLUMN failure_reason TEXT NULL;

CREATE TABLE attempt_events
(
    id TEXT PRIMARY KEY CHECK (length(id)=26),
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    project_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('log','toolCall','note','diff')),
    content TEXT NOT NULL CHECK (length(content) BETWEEN 1 AND 100000),
    occurred_at TEXT NOT NULL,
    UNIQUE (tenant_id,id),
    FOREIGN KEY (tenant_id,project_id,attempt_id)
        REFERENCES work_attempts(tenant_id,project_id,id)
);

CREATE INDEX ix_solicitations_board_project
    ON solicitations (tenant_id,project_id,id) WHERE is_internal=0;
CREATE INDEX ix_demands_board_project
    ON demands (tenant_id,project_id,id) WHERE is_internal=0;
CREATE INDEX ix_work_tasks_board_project
    ON work_tasks (tenant_id,project_id,board_state,id);
CREATE INDEX ix_attempt_events_attempt ON attempt_events (tenant_id,attempt_id,occurred_at,id);
