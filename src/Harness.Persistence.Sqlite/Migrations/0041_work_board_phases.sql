ALTER TABLE demands ADD COLUMN phase_name TEXT NULL CHECK (phase_name IS NULL OR length(phase_name) BETWEEN 1 AND 200);
ALTER TABLE work_tasks ADD COLUMN phase_name TEXT NULL CHECK (phase_name IS NULL OR length(phase_name) BETWEEN 1 AND 200);

CREATE INDEX ix_demands_project_phase ON demands (tenant_id,project_id,phase_name,id);
CREATE INDEX ix_work_tasks_project_phase ON work_tasks (tenant_id,project_id,phase_name,updated_at,id);
