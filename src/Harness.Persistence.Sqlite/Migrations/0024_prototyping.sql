ALTER TABLE projects ADD COLUMN prototyping_mode TEXT NOT NULL DEFAULT 'autonomousGeneration'
    CHECK(prototyping_mode IN('externalPrototype','guidelinesOnly','autonomousGeneration','notApplicable'));
ALTER TABLE projects ADD COLUMN prototyping_waiver_reason TEXT NULL;
ALTER TABLE projects ADD COLUMN prototyping_waiver_granted_at TEXT NULL;

CREATE TRIGGER projects_prototyping_waiver_insert
BEFORE INSERT ON projects WHEN
    (NEW.prototyping_mode='notApplicable') <> (NEW.prototyping_waiver_reason IS NOT NULL AND NEW.prototyping_waiver_granted_at IS NOT NULL)
BEGIN SELECT RAISE(ABORT, 'invalid prototyping waiver'); END;
CREATE TRIGGER projects_prototyping_waiver_update
BEFORE UPDATE OF prototyping_mode,prototyping_waiver_reason,prototyping_waiver_granted_at ON projects WHEN
    (NEW.prototyping_mode='notApplicable') <> (NEW.prototyping_waiver_reason IS NOT NULL AND NEW.prototyping_waiver_granted_at IS NOT NULL)
BEGIN SELECT RAISE(ABORT, 'invalid prototyping waiver'); END;

CREATE TABLE prototypes
(
    tenant_id TEXT NOT NULL, id TEXT NOT NULL CHECK(length(id)=26), project_id TEXT NOT NULL,
    name TEXT NOT NULL, description TEXT NOT NULL DEFAULT '', state TEXT NOT NULL DEFAULT 'draft'
        CHECK(state IN('draft','generating','ready','published','archived')),
    url TEXT NULL, thumbnail_url TEXT NULL, source_document_id TEXT NULL,
    created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
    PRIMARY KEY(tenant_id,id), FOREIGN KEY(tenant_id,project_id) REFERENCES projects(tenant_id,id)
);
CREATE TABLE visual_references
(
    tenant_id TEXT NOT NULL, id TEXT NOT NULL CHECK(length(id)=26), project_id TEXT NOT NULL,
    prototype_id TEXT NULL, title TEXT NOT NULL, image_url TEXT NOT NULL,
    source TEXT NOT NULL CHECK(source IN('upload','url','generated')), tags_json TEXT NOT NULL DEFAULT '[]'
        CHECK(json_valid(tags_json) AND json_type(tags_json)='array'), created_at TEXT NOT NULL, deleted_at TEXT NULL,
    PRIMARY KEY(tenant_id,id), FOREIGN KEY(tenant_id,project_id) REFERENCES projects(tenant_id,id),
    FOREIGN KEY(tenant_id,prototype_id) REFERENCES prototypes(tenant_id,id)
);
CREATE INDEX ix_prototypes_project ON prototypes(tenant_id,project_id,id) WHERE deleted_at IS NULL;
CREATE INDEX ix_visual_references_project ON visual_references(tenant_id,project_id,id) WHERE deleted_at IS NULL;
