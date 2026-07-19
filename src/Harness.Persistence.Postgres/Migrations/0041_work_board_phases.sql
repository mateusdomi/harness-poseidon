ALTER TABLE harness.demands ADD COLUMN phase_name varchar(200) NULL;
ALTER TABLE harness.work_tasks ADD COLUMN phase_name varchar(200) NULL;

CREATE INDEX ix_demands_project_phase ON harness.demands (tenant_id,project_id,phase_name,id);
CREATE INDEX ix_work_tasks_project_phase ON harness.work_tasks (tenant_id,project_id,phase_name,updated_at,id);
