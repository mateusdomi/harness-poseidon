ALTER TABLE work_tasks ADD COLUMN archived_at TEXT NULL;

CREATE INDEX ix_work_tasks_board_archive
    ON work_tasks (tenant_id, project_id, archived_at, board_state, id);
