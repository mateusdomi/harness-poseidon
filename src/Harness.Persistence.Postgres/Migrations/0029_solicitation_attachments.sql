CREATE TABLE harness.solicitation_attachments
(
    tenant_id char(26) NOT NULL,
    id char(26) NOT NULL,
    solicitation_id char(26) NOT NULL,
    file_name varchar(200) NOT NULL CHECK (length(file_name) > 0),
    content_type varchar(200) NOT NULL CHECK (length(content_type) > 0),
    size_bytes bigint NOT NULL CHECK (size_bytes > 0),
    sha256 char(64) NOT NULL,
    state varchar(20) NOT NULL CHECK (state IN ('quarantined', 'accepted')),
    storage_path text NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, solicitation_id, sha256),
    FOREIGN KEY (solicitation_id) REFERENCES harness.solicitations(id)
);

CREATE INDEX ix_solicitation_attachments_solicitation
    ON harness.solicitation_attachments (tenant_id, solicitation_id, id);
