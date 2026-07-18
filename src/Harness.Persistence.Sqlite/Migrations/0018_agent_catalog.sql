CREATE TABLE agent_definitions
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    agent_key TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    role TEXT NOT NULL CHECK (role IN ('chief', 'specialist')),
    specialty TEXT NULL,
    description TEXT NOT NULL,
    default_model_id TEXT NULL,
    skill_ids_json TEXT NOT NULL DEFAULT '[]'
        CHECK (json_valid(skill_ids_json) AND json_type(skill_ids_json) = 'array'),
    tool_ids_json TEXT NOT NULL DEFAULT '[]'
        CHECK (json_valid(tool_ids_json) AND json_type(tool_ids_json) = 'array')
);

INSERT INTO agent_definitions
    (id, agent_key, name, role, specialty, description)
VALUES
    ('01ARZ3NDEKTSV4RRFFQ69G5FAV', 'chief-orchestrator', 'Chief Orchestrator', 'chief', NULL,
     'Coordinates the project, delegates work, enforces gates, and reports progress to the user.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FAW', 'product-requirements-analyst', 'Product/Requirements Analyst', 'specialist', 'Product and requirements',
     'Turns user intent into traceable requirements, acceptance criteria, and prioritized demands.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FAX', 'software-architect', 'Software Architect', 'specialist', 'Software architecture',
     'Defines architecture, boundaries, quality attributes, and durable technical decisions.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FAY', 'software-engineer', 'Software Engineer', 'specialist', 'Software implementation',
     'Implements production changes with tests, evidence, and recoverable checkpoints.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FAZ', 'critic-qa', 'Critic/QA', 'specialist', 'Critical review and quality assurance',
     'Challenges assumptions, reviews evidence, and verifies functional and non-functional quality.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FB0', 'technical-writer', 'Technical Writer', 'specialist', 'Technical documentation',
     'Maintains clear, traceable, and versioned product and operational documentation.');

CREATE TABLE agents
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    definition_id TEXT NOT NULL REFERENCES agent_definitions(id),
    project_id TEXT NULL,
    name TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('working', 'idle', 'waiting', 'error', 'outOfQuota')),
    current_task_id TEXT NULL,
    model_id TEXT NULL,
    lease_fencing_token INTEGER NULL CHECK (lease_fencing_token IS NULL OR lease_fencing_token >= 0),
    lease_expires_at TEXT NULL,
    tasks_completed INTEGER NOT NULL DEFAULT 0 CHECK (tasks_completed >= 0),
    tokens_input INTEGER NOT NULL DEFAULT 0 CHECK (tokens_input >= 0),
    tokens_output INTEGER NOT NULL DEFAULT 0 CHECK (tokens_output >= 0),
    cost_usd NUMERIC NOT NULL DEFAULT 0 CHECK (cost_usd >= 0),
    uptime_ms INTEGER NOT NULL DEFAULT 0 CHECK (uptime_ms >= 0),
    last_heartbeat_at TEXT NULL,
    created_at TEXT NOT NULL,
    retired_at TEXT NULL,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id),
    CHECK ((lease_fencing_token IS NULL) = (lease_expires_at IS NULL))
);

CREATE INDEX ix_agents_tenant_project ON agents (tenant_id, project_id, id);
CREATE INDEX ix_agents_definition ON agents (definition_id, id);

INSERT INTO agents
    (id, tenant_id, definition_id, project_id, name, state,
     lease_fencing_token, lease_expires_at, last_heartbeat_at, created_at)
SELECT p.chief_agent_id, p.tenant_id, '01ARZ3NDEKTSV4RRFFQ69G5FAV', p.id,
       'Chief — ' || CASE WHEN p.project_key = '' THEN p.name ELSE p.project_key END,
       CASE WHEN p.state = 'paused' THEN 'waiting' ELSE 'idle' END,
       1, datetime(p.created_at, '+1 minute'), p.created_at, p.created_at
FROM projects p
WHERE length(p.chief_agent_id) = 26
  AND NOT EXISTS (SELECT 1 FROM agents a WHERE a.id = p.chief_agent_id);
