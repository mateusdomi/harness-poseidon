ALTER TABLE harness.projects ADD COLUMN prototyping_mode varchar(30) NOT NULL DEFAULT 'autonomousGeneration'
    CHECK (prototyping_mode IN ('externalPrototype', 'guidelinesOnly', 'autonomousGeneration', 'notApplicable'));
ALTER TABLE harness.projects ADD COLUMN prototyping_waiver_reason text NULL;
ALTER TABLE harness.projects ADD COLUMN prototyping_waiver_granted_at timestamptz NULL;

ALTER TABLE harness.projects ADD CONSTRAINT ck_projects_prototyping_waiver CHECK
    ((prototyping_mode = 'notApplicable') =
     (prototyping_waiver_reason IS NOT NULL AND prototyping_waiver_granted_at IS NOT NULL));

CREATE TABLE harness.prototypes
(
    tenant_id char(26) NOT NULL,
    id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    name varchar(200) NOT NULL,
    description varchar(4000) NOT NULL DEFAULT '',
    state varchar(20) NOT NULL DEFAULT 'draft'
        CHECK (state IN ('draft', 'generating', 'ready', 'published', 'archived')),
    url text NULL,
    thumbnail_url text NULL,
    source_document_id char(26) NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    PRIMARY KEY (tenant_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id)
);

CREATE TABLE harness.visual_references
(
    tenant_id char(26) NOT NULL,
    id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    prototype_id char(26) NULL,
    title varchar(200) NOT NULL,
    image_url text NOT NULL,
    source varchar(20) NOT NULL CHECK (source IN ('upload', 'url', 'generated')),
    tags_json jsonb NOT NULL DEFAULT '[]' CHECK (jsonb_typeof(tags_json) = 'array'),
    created_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    PRIMARY KEY (tenant_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id),
    FOREIGN KEY (tenant_id, prototype_id) REFERENCES harness.prototypes(tenant_id, id)
);

CREATE INDEX ix_prototypes_project
    ON harness.prototypes (tenant_id, project_id, id) WHERE deleted_at IS NULL;
CREATE INDEX ix_visual_references_project
    ON harness.visual_references (tenant_id, project_id, id) WHERE deleted_at IS NULL;
