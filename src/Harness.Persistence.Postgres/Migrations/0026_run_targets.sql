CREATE TABLE harness.run_targets
(
    tenant_id char(26) NOT NULL,
    id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    fingerprint char(64) NOT NULL,
    name varchar(200) NOT NULL,
    kind varchar(20) NOT NULL CHECK (kind IN ('http', 'tcp', 'process')),
    url text NULL,
    port integer NULL CHECK (port IS NULL OR port BETWEEN 1 AND 65535),
    state varchar(20) NOT NULL DEFAULT 'stopped' CHECK (state IN ('running', 'stopped', 'unknown')),
    working_directory text NOT NULL,
    executable text NOT NULL,
    arguments_json jsonb NOT NULL CHECK (jsonb_typeof(arguments_json) = 'array'),
    environment_json jsonb NOT NULL CHECK (jsonb_typeof(environment_json) = 'object'),
    detected_at timestamptz NOT NULL,
    last_check_at timestamptz NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, project_id, fingerprint),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id)
);

CREATE INDEX ix_run_targets_project ON harness.run_targets (tenant_id, project_id, id);
