CREATE TABLE harness.documents
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    title varchar(500) NOT NULL,
    kind varchar(20) NOT NULL CHECK (kind IN ('prd', 'spec', 'design', 'runbook', 'note', 'report')),
    state varchar(30) NOT NULL CHECK (state IN
        ('planned', 'in_elaboration', 'in_review', 'awaiting_approval',
         'approved', 'outdated', 'superseded', 'not_applicable')),
    current_version integer NOT NULL CHECK (current_version > 0),
    phase_name varchar(200) NULL,
    inconsistent boolean NOT NULL DEFAULT false,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id),
    CHECK (length(trim(title)) > 0),
    CHECK (phase_name IS NULL OR length(trim(phase_name)) > 0)
);

CREATE TABLE harness.document_versions
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    document_id char(26) NOT NULL,
    version integer NOT NULL CHECK (version > 0),
    catalog_path varchar(1024) NOT NULL,
    content_hash char(64) NOT NULL,
    supersedes_id char(26) NULL,
    author_kind varchar(20) NOT NULL CHECK (author_kind IN ('user', 'chief', 'agent')),
    author_id char(26) NULL,
    created_at timestamptz NOT NULL,
    UNIQUE (document_id, version),
    UNIQUE (document_id, id),
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, document_id)
        REFERENCES harness.documents(tenant_id, project_id, id),
    FOREIGN KEY (document_id, supersedes_id)
        REFERENCES harness.document_versions(document_id, id),
    CHECK (length(trim(catalog_path)) > 0 AND left(catalog_path, 1) <> '/'),
    CHECK (content_hash ~ '^[0-9A-F]{64}$'),
    CHECK ((version = 1 AND supersedes_id IS NULL) OR
           (version > 1 AND supersedes_id IS NOT NULL))
);

CREATE TABLE harness.document_classifications
(
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    document_id char(26) NOT NULL,
    label varchar(100) NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal > 0),
    PRIMARY KEY (document_id, label),
    UNIQUE (document_id, ordinal),
    FOREIGN KEY (tenant_id, project_id, document_id)
        REFERENCES harness.documents(tenant_id, project_id, id),
    CHECK (length(trim(label)) > 0)
);

CREATE TABLE harness.document_approval_requests
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    document_id char(26) NOT NULL,
    document_version_id char(26) NOT NULL,
    title varchar(500) NOT NULL,
    description varchar(10000) NOT NULL,
    priority varchar(20) NOT NULL CHECK (priority IN ('low', 'medium', 'high', 'critical')),
    due_at timestamptz NULL,
    state varchar(20) NOT NULL CHECK (state IN ('pending', 'approved', 'rejected', 'cancelled')),
    requested_by_agent_id char(26) NOT NULL,
    requested_at timestamptz NOT NULL,
    resolved_by_profile_id char(26) NULL,
    resolved_at timestamptz NULL,
    resolution_note varchar(10000) NULL,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, document_id)
        REFERENCES harness.documents(tenant_id, project_id, id),
    FOREIGN KEY (document_id, document_version_id)
        REFERENCES harness.document_versions(document_id, id),
    CHECK (length(trim(title)) > 0),
    CHECK (length(trim(description)) > 0),
    CHECK ((state = 'pending' AND resolved_by_profile_id IS NULL AND
            resolved_at IS NULL AND resolution_note IS NULL) OR
           (state = 'approved' AND resolved_by_profile_id IS NOT NULL AND
            resolved_at IS NOT NULL) OR
           (state = 'rejected' AND resolved_by_profile_id IS NOT NULL AND
            resolved_at IS NOT NULL AND resolution_note IS NOT NULL AND
            length(trim(resolution_note)) > 0) OR
           (state = 'cancelled' AND resolved_at IS NOT NULL AND
            resolution_note IS NOT NULL AND length(trim(resolution_note)) > 0))
);

CREATE TABLE harness.document_state_transitions
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    document_id char(26) NOT NULL,
    document_version bigint NOT NULL CHECK (document_version > 0),
    from_state varchar(30) NOT NULL CHECK (from_state IN
        ('planned', 'in_elaboration', 'in_review', 'awaiting_approval',
         'approved', 'outdated', 'superseded', 'not_applicable')),
    to_state varchar(30) NOT NULL CHECK (to_state IN
        ('planned', 'in_elaboration', 'in_review', 'awaiting_approval',
         'approved', 'outdated', 'superseded', 'not_applicable')),
    note varchar(10000) NULL,
    actor_kind varchar(20) NOT NULL CHECK (actor_kind IN ('user', 'chief', 'agent', 'system')),
    actor_id char(26) NULL,
    occurred_at timestamptz NOT NULL,
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, document_id)
        REFERENCES harness.documents(tenant_id, project_id, id),
    CHECK (from_state <> to_state),
    CHECK (note IS NULL OR length(trim(note)) > 0)
);

CREATE UNIQUE INDEX ux_document_approval_requests_pending
    ON harness.document_approval_requests (document_id) WHERE state = 'pending';
CREATE INDEX ix_documents_project_state
    ON harness.documents (tenant_id, project_id, state, updated_at, id);
CREATE INDEX ix_documents_orphans
    ON harness.documents (tenant_id, project_id, updated_at, id) WHERE phase_name IS NULL;
CREATE INDEX ix_document_versions_document
    ON harness.document_versions (document_id, version);
CREATE INDEX ix_document_approval_requests_queue
    ON harness.document_approval_requests
       (tenant_id, project_id, state, due_at, priority, requested_at, id);
CREATE INDEX ix_document_state_transitions_document
    ON harness.document_state_transitions (document_id, occurred_at, id);

CREATE OR REPLACE FUNCTION harness.reject_immutable_document_row()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'document history is immutable' USING ERRCODE = '23000';
END;
$$;

CREATE TRIGGER tr_document_versions_no_mutation
BEFORE UPDATE OR DELETE ON harness.document_versions
FOR EACH ROW EXECUTE FUNCTION harness.reject_immutable_document_row();

CREATE TRIGGER tr_document_state_transitions_no_mutation
BEFORE UPDATE OR DELETE ON harness.document_state_transitions
FOR EACH ROW EXECUTE FUNCTION harness.reject_immutable_document_row();
