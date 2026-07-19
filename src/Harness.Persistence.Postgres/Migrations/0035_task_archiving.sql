ALTER TABLE harness.work_tasks ADD COLUMN archived_at timestamptz NULL;

CREATE INDEX ix_work_tasks_board_archive
    ON harness.work_tasks (tenant_id, project_id, archived_at, board_state, id);
