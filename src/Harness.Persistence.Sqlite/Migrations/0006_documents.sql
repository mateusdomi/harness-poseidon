CREATE TABLE documents
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 500),
    kind TEXT NOT NULL CHECK (kind IN ('prd', 'spec', 'design', 'runbook', 'note', 'report')),
    state TEXT NOT NULL CHECK (state IN
        ('planned', 'in_elaboration', 'in_review', 'awaiting_approval',
         'approved', 'outdated', 'superseded', 'not_applicable')),
    current_version INTEGER NOT NULL CHECK (current_version > 0),
    phase_name TEXT NULL CHECK (phase_name IS NULL OR length(phase_name) BETWEEN 1 AND 200),
    inconsistent INTEGER NOT NULL DEFAULT 0 CHECK (inconsistent IN (0, 1)),
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id)
);

CREATE TABLE document_versions
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    document_id TEXT NOT NULL,
    version INTEGER NOT NULL CHECK (version > 0),
    catalog_path TEXT NOT NULL CHECK
        (length(catalog_path) BETWEEN 1 AND 1024 AND substr(catalog_path, 1, 1) <> '/'),
    content_hash TEXT NOT NULL CHECK
        (length(content_hash) = 64 AND content_hash = upper(content_hash) AND
         content_hash NOT GLOB '*[^0-9A-F]*'),
    supersedes_id TEXT NULL,
    author_kind TEXT NOT NULL CHECK (author_kind IN ('user', 'chief', 'agent')),
    author_id TEXT NULL CHECK (author_id IS NULL OR length(author_id) = 26),
    created_at TEXT NOT NULL,
    UNIQUE (document_id, version),
    UNIQUE (document_id, id),
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, document_id)
        REFERENCES documents(tenant_id, project_id, id),
    FOREIGN KEY (document_id, supersedes_id)
        REFERENCES document_versions(document_id, id),
    CHECK ((version = 1 AND supersedes_id IS NULL) OR
           (version > 1 AND supersedes_id IS NOT NULL))
);

CREATE TABLE document_classifications
(
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    document_id TEXT NOT NULL,
    label TEXT NOT NULL CHECK (length(label) BETWEEN 1 AND 100),
    ordinal INTEGER NOT NULL CHECK (ordinal > 0),
    PRIMARY KEY (document_id, label),
    UNIQUE (document_id, ordinal),
    FOREIGN KEY (tenant_id, project_id, document_id)
        REFERENCES documents(tenant_id, project_id, id)
);

CREATE TABLE document_approval_requests
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    document_id TEXT NOT NULL,
    document_version_id TEXT NOT NULL,
    title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 500),
    description TEXT NOT NULL CHECK (length(description) BETWEEN 1 AND 10000),
    priority TEXT NOT NULL CHECK (priority IN ('low', 'medium', 'high', 'critical')),
    due_at TEXT NULL,
    state TEXT NOT NULL CHECK (state IN ('pending', 'approved', 'rejected', 'cancelled')),
    requested_by_agent_id TEXT NOT NULL CHECK (length(requested_by_agent_id) = 26),
    requested_at TEXT NOT NULL,
    resolved_by_profile_id TEXT NULL CHECK
        (resolved_by_profile_id IS NULL OR length(resolved_by_profile_id) = 26),
    resolved_at TEXT NULL,
    resolution_note TEXT NULL CHECK
        (resolution_note IS NULL OR length(resolution_note) BETWEEN 1 AND 10000),
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, document_id)
        REFERENCES documents(tenant_id, project_id, id),
    FOREIGN KEY (document_id, document_version_id)
        REFERENCES document_versions(document_id, id),
    CHECK ((state = 'pending' AND resolved_by_profile_id IS NULL AND
            resolved_at IS NULL AND resolution_note IS NULL) OR
           (state = 'approved' AND resolved_by_profile_id IS NOT NULL AND
            resolved_at IS NOT NULL) OR
           (state = 'rejected' AND resolved_by_profile_id IS NOT NULL AND
            resolved_at IS NOT NULL AND resolution_note IS NOT NULL) OR
           (state = 'cancelled' AND resolved_at IS NOT NULL AND resolution_note IS NOT NULL))
);

CREATE TABLE document_state_transitions
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    document_id TEXT NOT NULL,
    document_version INTEGER NOT NULL CHECK (document_version > 0),
    from_state TEXT NOT NULL CHECK (from_state IN
        ('planned', 'in_elaboration', 'in_review', 'awaiting_approval',
         'approved', 'outdated', 'superseded', 'not_applicable')),
    to_state TEXT NOT NULL CHECK (to_state IN
        ('planned', 'in_elaboration', 'in_review', 'awaiting_approval',
         'approved', 'outdated', 'superseded', 'not_applicable')),
    note TEXT NULL CHECK (note IS NULL OR length(note) BETWEEN 1 AND 10000),
    actor_kind TEXT NOT NULL CHECK (actor_kind IN ('user', 'chief', 'agent', 'system')),
    actor_id TEXT NULL CHECK (actor_id IS NULL OR length(actor_id) = 26),
    occurred_at TEXT NOT NULL,
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, document_id)
        REFERENCES documents(tenant_id, project_id, id),
    CHECK (from_state <> to_state)
);

CREATE UNIQUE INDEX ux_document_approval_requests_pending
    ON document_approval_requests (document_id) WHERE state = 'pending';
CREATE INDEX ix_documents_project_state
    ON documents (tenant_id, project_id, state, updated_at, id);
CREATE INDEX ix_documents_orphans
    ON documents (tenant_id, project_id, updated_at, id) WHERE phase_name IS NULL;
CREATE INDEX ix_document_versions_document
    ON document_versions (document_id, version);
CREATE INDEX ix_document_approval_requests_queue
    ON document_approval_requests (tenant_id, project_id, state, due_at, priority, requested_at, id);
CREATE INDEX ix_document_state_transitions_document
    ON document_state_transitions (document_id, occurred_at, id);

CREATE TRIGGER tr_document_versions_no_update
BEFORE UPDATE ON document_versions
BEGIN
    SELECT RAISE(ABORT, 'document versions are immutable');
END;

CREATE TRIGGER tr_document_versions_no_delete
BEFORE DELETE ON document_versions
BEGIN
    SELECT RAISE(ABORT, 'document versions are immutable');
END;

CREATE TRIGGER tr_document_state_transitions_no_update
BEFORE UPDATE ON document_state_transitions
BEGIN
    SELECT RAISE(ABORT, 'document transitions are append-only');
END;

CREATE TRIGGER tr_document_state_transitions_no_delete
BEFORE DELETE ON document_state_transitions
BEGIN
    SELECT RAISE(ABORT, 'document transitions are append-only');
END;
