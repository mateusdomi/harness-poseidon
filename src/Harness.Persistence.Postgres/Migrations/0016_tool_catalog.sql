CREATE TABLE harness.skills
(
    id char(26) PRIMARY KEY,
    skill_key varchar(100) NOT NULL UNIQUE,
    name varchar(200) NOT NULL,
    description text NOT NULL,
    version varchar(50) NOT NULL,
    state varchar(20) NOT NULL CHECK (state IN ('enabled', 'disabled', 'error'))
);

CREATE TABLE harness.tools
(
    id char(26) PRIMARY KEY,
    tool_key varchar(100) NOT NULL UNIQUE,
    name varchar(200) NOT NULL,
    description text NOT NULL,
    kind varchar(20) NOT NULL CHECK (kind IN ('builtin', 'mcp', 'plugin')),
    state varchar(20) NOT NULL CHECK (state IN ('enabled', 'disabled', 'error')),
    input_schema_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    output_schema_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    maximum_risk_tier varchar(20) NOT NULL DEFAULT 'medium'
        CHECK (maximum_risk_tier IN ('low', 'medium', 'high', 'critical'))
);

CREATE TABLE harness.plugins
(
    id char(26) PRIMARY KEY,
    plugin_key varchar(100) NOT NULL UNIQUE,
    name varchar(200) NOT NULL,
    version varchar(50) NOT NULL,
    description text NOT NULL,
    state varchar(20) NOT NULL CHECK (state IN ('enabled', 'disabled', 'error')),
    checksum_sha256 char(64) NOT NULL,
    permissions_json jsonb NOT NULL DEFAULT '[]'::jsonb
        CHECK (jsonb_typeof(permissions_json) = 'array'),
    risk_tier varchar(20) NOT NULL CHECK (risk_tier IN ('low', 'medium', 'high', 'critical'))
);

CREATE TABLE harness.plugin_tools
(
    plugin_id char(26) NOT NULL REFERENCES harness.plugins(id),
    tool_id char(26) NOT NULL REFERENCES harness.tools(id),
    PRIMARY KEY (plugin_id, tool_id)
);

CREATE TABLE harness.mcp_servers
(
    id char(26) PRIMARY KEY,
    name varchar(200) NOT NULL UNIQUE,
    transport varchar(20) NOT NULL CHECK (transport IN ('stdio', 'http')),
    endpoint text NOT NULL,
    state varchar(20) NOT NULL CHECK (state IN ('enabled', 'disabled', 'error')),
    tool_count integer NOT NULL DEFAULT 0 CHECK (tool_count >= 0),
    protocol_version varchar(50) NOT NULL DEFAULT '2025-11-25',
    experimental_rc_enabled boolean NOT NULL DEFAULT false
);

INSERT INTO harness.skills (id, skill_key, name, description, version, state) VALUES
('01ARZ3NDEKTSV4RRFFQ69G5FC1', 'planning', 'Planning', 'Break demands into traceable executable tasks.', '1.0.0', 'enabled'),
('01ARZ3NDEKTSV4RRFFQ69G5FC2', 'requirements', 'Requirements analysis', 'Elicit and validate requirements and acceptance criteria.', '1.0.0', 'enabled'),
('01ARZ3NDEKTSV4RRFFQ69G5FC3', 'architecture', 'Software architecture', 'Design boundaries, quality attributes, and technical decisions.', '1.0.0', 'enabled'),
('01ARZ3NDEKTSV4RRFFQ69G5FC4', 'testing-review', 'Testing and review', 'Execute tests and independent critical review.', '1.0.0', 'enabled'),
('01ARZ3NDEKTSV4RRFFQ69G5FC5', 'technical-writing', 'Technical writing', 'Create and maintain versioned technical documentation.', '1.0.0', 'enabled');

INSERT INTO harness.tools (id, tool_key, name, description, kind, state, input_schema_json, output_schema_json, maximum_risk_tier) VALUES
('01ARZ3NDEKTSV4RRFFQ69G5FD1', 'shell', 'Shell', 'Execute allowlisted commands in the project sandbox.', 'builtin', 'enabled', '{"type":"object"}', '{"type":"object"}', 'critical'),
('01ARZ3NDEKTSV4RRFFQ69G5FD2', 'filesystem', 'Filesystem', 'Read and write claimed workspace paths.', 'builtin', 'enabled', '{"type":"object"}', '{"type":"object"}', 'high'),
('01ARZ3NDEKTSV4RRFFQ69G5FD3', 'git', 'Git', 'Inspect and checkpoint changes in managed repositories.', 'builtin', 'enabled', '{"type":"object"}', '{"type":"object"}', 'high'),
('01ARZ3NDEKTSV4RRFFQ69G5FD4', 'web.search', 'Web search', 'Search approved internet sources through policy-controlled egress.', 'builtin', 'enabled', '{"type":"object"}', '{"type":"object"}', 'medium'),
('01ARZ3NDEKTSV4RRFFQ69G5FD5', 'github.pr', 'GitHub pull requests', 'Inspect and manage pull requests through MCP.', 'mcp', 'disabled', '{"type":"object"}', '{"type":"object"}', 'high'),
('01ARZ3NDEKTSV4RRFFQ69G5FD6', 'artifact.render', 'Artifact renderer', 'Render documents and visual evidence in the sandbox.', 'plugin', 'enabled', '{"type":"object"}', '{"type":"object"}', 'medium');

INSERT INTO harness.plugins (id, plugin_key, name, version, description, state, checksum_sha256, permissions_json, risk_tier) VALUES
('01ARZ3NDEKTSV4RRFFQ69G5FE1', 'github-integration', 'GitHub integration', '1.0.0', 'Provides pull-request operations through the stable MCP boundary.', 'disabled', 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA', '["network:github.com","repository:pull-request"]', 'high'),
('01ARZ3NDEKTSV4RRFFQ69G5FE2', 'artifact-renderer', 'Artifact renderer', '1.0.0', 'Produces local preview artifacts without external network access.', 'enabled', 'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB', '["filesystem:artifacts"]', 'medium');

INSERT INTO harness.plugin_tools (plugin_id, tool_id) VALUES
('01ARZ3NDEKTSV4RRFFQ69G5FE1', '01ARZ3NDEKTSV4RRFFQ69G5FD5'),
('01ARZ3NDEKTSV4RRFFQ69G5FE2', '01ARZ3NDEKTSV4RRFFQ69G5FD6');

INSERT INTO harness.mcp_servers (id, name, transport, endpoint, state, tool_count) VALUES
('01ARZ3NDEKTSV4RRFFQ69G5FF1', 'github-mcp', 'http', 'https://api.githubcopilot.com/mcp/', 'disabled', 1),
('01ARZ3NDEKTSV4RRFFQ69G5FF2', 'filesystem-mcp', 'stdio', 'harness-mcp-filesystem --workspace ${WORKSPACE}', 'enabled', 3);

UPDATE harness.agent_definitions SET
    skill_ids_json = '["01ARZ3NDEKTSV4RRFFQ69G5FC1","01ARZ3NDEKTSV4RRFFQ69G5FC5"]'::jsonb,
    tool_ids_json = '["01ARZ3NDEKTSV4RRFFQ69G5FD1","01ARZ3NDEKTSV4RRFFQ69G5FD2","01ARZ3NDEKTSV4RRFFQ69G5FD3","01ARZ3NDEKTSV4RRFFQ69G5FD4"]'::jsonb
WHERE agent_key = 'chief-orchestrator';
UPDATE harness.agent_definitions SET skill_ids_json = '["01ARZ3NDEKTSV4RRFFQ69G5FC1","01ARZ3NDEKTSV4RRFFQ69G5FC2"]'::jsonb, tool_ids_json = '["01ARZ3NDEKTSV4RRFFQ69G5FD2","01ARZ3NDEKTSV4RRFFQ69G5FD4"]'::jsonb WHERE agent_key = 'product-requirements-analyst';
UPDATE harness.agent_definitions SET skill_ids_json = '["01ARZ3NDEKTSV4RRFFQ69G5FC1","01ARZ3NDEKTSV4RRFFQ69G5FC3"]'::jsonb, tool_ids_json = '["01ARZ3NDEKTSV4RRFFQ69G5FD2","01ARZ3NDEKTSV4RRFFQ69G5FD3","01ARZ3NDEKTSV4RRFFQ69G5FD4"]'::jsonb WHERE agent_key = 'software-architect';
UPDATE harness.agent_definitions SET skill_ids_json = '["01ARZ3NDEKTSV4RRFFQ69G5FC4"]'::jsonb, tool_ids_json = '["01ARZ3NDEKTSV4RRFFQ69G5FD1","01ARZ3NDEKTSV4RRFFQ69G5FD2","01ARZ3NDEKTSV4RRFFQ69G5FD3"]'::jsonb WHERE agent_key = 'software-engineer';
UPDATE harness.agent_definitions SET skill_ids_json = '["01ARZ3NDEKTSV4RRFFQ69G5FC4"]'::jsonb, tool_ids_json = '["01ARZ3NDEKTSV4RRFFQ69G5FD1","01ARZ3NDEKTSV4RRFFQ69G5FD3"]'::jsonb WHERE agent_key = 'critic-qa';
UPDATE harness.agent_definitions SET skill_ids_json = '["01ARZ3NDEKTSV4RRFFQ69G5FC5"]'::jsonb, tool_ids_json = '["01ARZ3NDEKTSV4RRFFQ69G5FD2","01ARZ3NDEKTSV4RRFFQ69G5FD4"]'::jsonb WHERE agent_key = 'technical-writer';
