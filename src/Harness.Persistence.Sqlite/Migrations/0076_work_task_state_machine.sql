CREATE TABLE work_tasks_state_machine
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    demand_id TEXT NOT NULL,
    title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 500),
    risk_tier TEXT NOT NULL CHECK (risk_tier IN ('low', 'medium', 'high', 'critical')),
    weight REAL NOT NULL CHECK (weight > 0),
    state TEXT NOT NULL CHECK
    (
        state IN
        (
            'draft',
            'triaged',
            'ready',
            'assigned',
            'running',
            'review',
            'awaiting_review',
            'blocked',
            'approved',
            'escalated',
            'merged',
            'done',
            'completed',
            'cancelled'
        )
    ),
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    source_demand_id TEXT NULL REFERENCES demands(id),
    board_state TEXT NOT NULL DEFAULT 'ready'
        CHECK
        (
            board_state IN
            (
                'backlog',
                'ready',
                'development',
                'review',
                'corrections',
                'testsGates',
                'blocked',
                'done'
            )
        ),
    priority TEXT NOT NULL DEFAULT 'medium'
        CHECK (priority IN ('low', 'medium', 'high', 'critical')),
    assignee_agent_id TEXT NULL,
    blocked_reason TEXT NULL,
    due_at TEXT NULL,
    archived_at TEXT NULL,
    phase_name TEXT NULL CHECK
        (phase_name IS NULL OR length(phase_name) BETWEEN 1 AND 200),
    card_type TEXT NOT NULL DEFAULT 'agent_task'
        CHECK (card_type IN ('feature', 'agent_task', 'human_gate', 'spike', 'decision')),
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, demand_id)
        REFERENCES demands(tenant_id, project_id, id)
);

INSERT INTO work_tasks_state_machine
(
    id,
    tenant_id,
    project_id,
    demand_id,
    title,
    risk_tier,
    weight,
    state,
    version,
    created_at,
    updated_at,
    source_demand_id,
    board_state,
    priority,
    assignee_agent_id,
    blocked_reason,
    due_at,
    archived_at,
    phase_name,
    card_type
)
SELECT
    id,
    tenant_id,
    project_id,
    demand_id,
    title,
    risk_tier,
    weight,
    state,
    version,
    created_at,
    updated_at,
    source_demand_id,
    board_state,
    priority,
    assignee_agent_id,
    blocked_reason,
    due_at,
    archived_at,
    phase_name,
    card_type
FROM work_tasks;

DROP TABLE work_tasks;
ALTER TABLE work_tasks_state_machine RENAME TO work_tasks;

CREATE INDEX ix_work_tasks_demand_state
    ON work_tasks (demand_id, state, created_at, id);
CREATE INDEX ix_work_tasks_board_project
    ON work_tasks (tenant_id, project_id, board_state, id);
CREATE INDEX ix_work_tasks_board_archive
    ON work_tasks (tenant_id, project_id, archived_at, board_state, id);
CREATE INDEX ix_work_tasks_project_phase
    ON work_tasks (tenant_id, project_id, phase_name, updated_at, id);
