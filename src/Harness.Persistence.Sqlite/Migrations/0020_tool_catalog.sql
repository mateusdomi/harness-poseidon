CREATE TABLE skills
(
    id TEXT PRIMARY KEY CHECK (length(id)=26),
    skill_key TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    version TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('enabled','disabled','error'))
);

CREATE TABLE tools
(
    id TEXT PRIMARY KEY CHECK (length(id)=26),
    tool_key TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('builtin','mcp','plugin')),
    state TEXT NOT NULL CHECK (state IN ('enabled','disabled','error')),
    input_schema_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(input_schema_json)),
    output_schema_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(output_schema_json)),
    maximum_risk_tier TEXT NOT NULL DEFAULT 'medium'
        CHECK (maximum_risk_tier IN ('low','medium','high','critical'))
);

CREATE TABLE plugins
(
    id TEXT PRIMARY KEY CHECK (length(id)=26),
    plugin_key TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    version TEXT NOT NULL,
    description TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('enabled','disabled','error')),
    checksum_sha256 TEXT NOT NULL CHECK (length(checksum_sha256)=64),
    permissions_json TEXT NOT NULL DEFAULT '[]'
        CHECK (json_valid(permissions_json) AND json_type(permissions_json)='array'),
    risk_tier TEXT NOT NULL CHECK (risk_tier IN ('low','medium','high','critical'))
);

CREATE TABLE plugin_tools
(
    plugin_id TEXT NOT NULL REFERENCES plugins(id),
    tool_id TEXT NOT NULL REFERENCES tools(id),
    PRIMARY KEY (plugin_id,tool_id)
);

CREATE TABLE mcp_servers
(
    id TEXT PRIMARY KEY CHECK (length(id)=26),
    name TEXT NOT NULL UNIQUE,
    transport TEXT NOT NULL CHECK (transport IN ('stdio','http')),
    endpoint TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('enabled','disabled','error')),
    tool_count INTEGER NOT NULL DEFAULT 0 CHECK (tool_count>=0),
    protocol_version TEXT NOT NULL DEFAULT '2025-11-25',
    experimental_rc_enabled INTEGER NOT NULL DEFAULT 0 CHECK (experimental_rc_enabled IN (0,1))
);

INSERT INTO skills (id,skill_key,name,description,version,state) VALUES
('01ARZ3NDEKTSV4RRFFQ69G5FC1','planning','Planning','Break demands into traceable executable tasks.','1.0.0','enabled'),
('01ARZ3NDEKTSV4RRFFQ69G5FC2','requirements','Requirements analysis','Elicit and validate requirements and acceptance criteria.','1.0.0','enabled'),
('01ARZ3NDEKTSV4RRFFQ69G5FC3','architecture','Software architecture','Design boundaries, quality attributes, and technical decisions.','1.0.0','enabled'),
('01ARZ3NDEKTSV4RRFFQ69G5FC4','testing-review','Testing and review','Execute tests and independent critical review.','1.0.0','enabled'),
('01ARZ3NDEKTSV4RRFFQ69G5FC5','technical-writing','Technical writing','Create and maintain versioned technical documentation.','1.0.0','enabled');

INSERT INTO tools (id,tool_key,name,description,kind,state,input_schema_json,output_schema_json,maximum_risk_tier) VALUES
('01ARZ3NDEKTSV4RRFFQ69G5FD1','shell','Shell','Execute allowlisted commands in the project sandbox.','builtin','enabled','{"type":"object"}','{"type":"object"}','critical'),
('01ARZ3NDEKTSV4RRFFQ69G5FD2','filesystem','Filesystem','Read and write claimed workspace paths.','builtin','enabled','{"type":"object"}','{"type":"object"}','high'),
('01ARZ3NDEKTSV4RRFFQ69G5FD3','git','Git','Inspect and checkpoint changes in managed repositories.','builtin','enabled','{"type":"object"}','{"type":"object"}','high'),
('01ARZ3NDEKTSV4RRFFQ69G5FD4','web.search','Web search','Search approved internet sources through policy-controlled egress.','builtin','enabled','{"type":"object"}','{"type":"object"}','medium'),
('01ARZ3NDEKTSV4RRFFQ69G5FD5','github.pr','GitHub pull requests','Inspect and manage pull requests through MCP.','mcp','disabled','{"type":"object"}','{"type":"object"}','high'),
('01ARZ3NDEKTSV4RRFFQ69G5FD6','artifact.render','Artifact renderer','Render documents and visual evidence in the sandbox.','plugin','enabled','{"type":"object"}','{"type":"object"}','medium');

INSERT INTO plugins (id,plugin_key,name,version,description,state,checksum_sha256,permissions_json,risk_tier) VALUES
('01ARZ3NDEKTSV4RRFFQ69G5FE1','github-integration','GitHub integration','1.0.0','Provides pull-request operations through the stable MCP boundary.','disabled','AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA','["network:github.com","repository:pull-request"]','high'),
('01ARZ3NDEKTSV4RRFFQ69G5FE2','artifact-renderer','Artifact renderer','1.0.0','Produces local preview artifacts without external network access.','enabled','BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB','["filesystem:artifacts"]','medium');

INSERT INTO plugin_tools (plugin_id,tool_id) VALUES
('01ARZ3NDEKTSV4RRFFQ69G5FE1','01ARZ3NDEKTSV4RRFFQ69G5FD5'),
('01ARZ3NDEKTSV4RRFFQ69G5FE2','01ARZ3NDEKTSV4RRFFQ69G5FD6');

INSERT INTO mcp_servers (id,name,transport,endpoint,state,tool_count) VALUES
('01ARZ3NDEKTSV4RRFFQ69G5FF1','github-mcp','http','https://api.githubcopilot.com/mcp/','disabled',1),
('01ARZ3NDEKTSV4RRFFQ69G5FF2','filesystem-mcp','stdio','harness-mcp-filesystem --workspace ${WORKSPACE}','enabled',3);

UPDATE agent_definitions SET
    skill_ids_json='["01ARZ3NDEKTSV4RRFFQ69G5FC1","01ARZ3NDEKTSV4RRFFQ69G5FC5"]',
    tool_ids_json='["01ARZ3NDEKTSV4RRFFQ69G5FD1","01ARZ3NDEKTSV4RRFFQ69G5FD2","01ARZ3NDEKTSV4RRFFQ69G5FD3","01ARZ3NDEKTSV4RRFFQ69G5FD4"]'
WHERE agent_key='chief-orchestrator';
UPDATE agent_definitions SET skill_ids_json='["01ARZ3NDEKTSV4RRFFQ69G5FC1","01ARZ3NDEKTSV4RRFFQ69G5FC2"]',tool_ids_json='["01ARZ3NDEKTSV4RRFFQ69G5FD2","01ARZ3NDEKTSV4RRFFQ69G5FD4"]' WHERE agent_key='product-requirements-analyst';
UPDATE agent_definitions SET skill_ids_json='["01ARZ3NDEKTSV4RRFFQ69G5FC1","01ARZ3NDEKTSV4RRFFQ69G5FC3"]',tool_ids_json='["01ARZ3NDEKTSV4RRFFQ69G5FD2","01ARZ3NDEKTSV4RRFFQ69G5FD3","01ARZ3NDEKTSV4RRFFQ69G5FD4"]' WHERE agent_key='software-architect';
UPDATE agent_definitions SET skill_ids_json='["01ARZ3NDEKTSV4RRFFQ69G5FC4"]',tool_ids_json='["01ARZ3NDEKTSV4RRFFQ69G5FD1","01ARZ3NDEKTSV4RRFFQ69G5FD2","01ARZ3NDEKTSV4RRFFQ69G5FD3"]' WHERE agent_key='software-engineer';
UPDATE agent_definitions SET skill_ids_json='["01ARZ3NDEKTSV4RRFFQ69G5FC4"]',tool_ids_json='["01ARZ3NDEKTSV4RRFFQ69G5FD1","01ARZ3NDEKTSV4RRFFQ69G5FD3"]' WHERE agent_key='critic-qa';
UPDATE agent_definitions SET skill_ids_json='["01ARZ3NDEKTSV4RRFFQ69G5FC5"]',tool_ids_json='["01ARZ3NDEKTSV4RRFFQ69G5FD2","01ARZ3NDEKTSV4RRFFQ69G5FD4"]' WHERE agent_key='technical-writer';
