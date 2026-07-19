CREATE TABLE harness.agent_definitions
(
    id char(26) PRIMARY KEY,
    agent_key varchar(100) NOT NULL UNIQUE,
    name varchar(200) NOT NULL,
    role varchar(50) NOT NULL CHECK (role IN ('chief', 'specialist')),
    specialty varchar(200) NULL,
    description text NOT NULL,
    default_model_id char(26) NULL,
    skill_ids_json jsonb NOT NULL DEFAULT '[]'::jsonb
        CHECK (jsonb_typeof(skill_ids_json) = 'array'),
    tool_ids_json jsonb NOT NULL DEFAULT '[]'::jsonb
        CHECK (jsonb_typeof(tool_ids_json) = 'array')
);

INSERT INTO harness.agent_definitions
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

CREATE TABLE harness.agents
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    definition_id char(26) NOT NULL REFERENCES harness.agent_definitions(id),
    project_id char(26) NULL,
    name varchar(300) NOT NULL,
    state varchar(50) NOT NULL CHECK (state IN ('working', 'idle', 'waiting', 'error', 'outOfQuota')),
    current_task_id char(26) NULL,
    model_id char(26) NULL,
    lease_fencing_token bigint NULL CHECK (lease_fencing_token IS NULL OR lease_fencing_token >= 0),
    lease_expires_at timestamptz NULL,
    tasks_completed bigint NOT NULL DEFAULT 0 CHECK (tasks_completed >= 0),
    tokens_input bigint NOT NULL DEFAULT 0 CHECK (tokens_input >= 0),
    tokens_output bigint NOT NULL DEFAULT 0 CHECK (tokens_output >= 0),
    cost_usd numeric NOT NULL DEFAULT 0 CHECK (cost_usd >= 0),
    uptime_ms bigint NOT NULL DEFAULT 0 CHECK (uptime_ms >= 0),
    last_heartbeat_at timestamptz NULL,
    created_at timestamptz NOT NULL,
    retired_at timestamptz NULL,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id),
    CHECK ((lease_fencing_token IS NULL) = (lease_expires_at IS NULL))
);

CREATE INDEX ix_agents_tenant_project ON harness.agents (tenant_id, project_id, id);
CREATE INDEX ix_agents_definition ON harness.agents (definition_id, id);

INSERT INTO harness.agents
    (id, tenant_id, definition_id, project_id, name, state,
     lease_fencing_token, lease_expires_at, last_heartbeat_at, created_at)
SELECT p.chief_agent_id, p.tenant_id, '01ARZ3NDEKTSV4RRFFQ69G5FAV', p.id,
       'Chief — ' || CASE WHEN p.project_key = '' THEN p.name ELSE p.project_key END,
       CASE WHEN p.state = 'paused' THEN 'waiting' ELSE 'idle' END,
       1, p.created_at + interval '1 minute', p.created_at, p.created_at
FROM harness.projects p
WHERE length(p.chief_agent_id) = 26
  AND NOT EXISTS (SELECT 1 FROM harness.agents a WHERE a.id = p.chief_agent_id);
