CREATE TABLE run_targets
(
    tenant_id TEXT NOT NULL,
    id TEXT NOT NULL CHECK(length(id)=26),
    project_id TEXT NOT NULL,
    fingerprint TEXT NOT NULL CHECK(length(fingerprint)=64),
    name TEXT NOT NULL,
    kind TEXT NOT NULL CHECK(kind IN('http','tcp','process')),
    url TEXT NULL,
    port INTEGER NULL CHECK(port IS NULL OR port BETWEEN 1 AND 65535),
    state TEXT NOT NULL DEFAULT 'stopped' CHECK(state IN('running','stopped','unknown')),
    working_directory TEXT NOT NULL,
    executable TEXT NOT NULL,
    arguments_json TEXT NOT NULL CHECK(json_valid(arguments_json) AND json_type(arguments_json)='array'),
    environment_json TEXT NOT NULL CHECK(json_valid(environment_json) AND json_type(environment_json)='object'),
    detected_at TEXT NOT NULL,
    last_check_at TEXT NULL,
    PRIMARY KEY(tenant_id,id),
    UNIQUE(tenant_id,project_id,fingerprint),
    FOREIGN KEY(tenant_id,project_id) REFERENCES projects(tenant_id,id)
);

CREATE INDEX ix_run_targets_project ON run_targets(tenant_id,project_id,id);
