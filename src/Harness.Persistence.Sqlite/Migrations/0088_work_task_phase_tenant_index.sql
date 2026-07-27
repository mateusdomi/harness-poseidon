-- PageTasksAsync aceita phase_name sem project_id (consulta de cards por fase em todo o tenant,
-- ex.: GET /work-board/tasks?phaseName=... sem projectId). O índice de 0041 lidera com project_id,
-- então esse filtro perde o prefixo e phase_name deixa de ser usável pelo índice. Este índice cobre
-- exatamente esse formato de consulta, sem alterar tabela, coluna ou constraint alguma.
CREATE INDEX IF NOT EXISTS ix_work_tasks_tenant_phase
    ON work_tasks (tenant_id, phase_name, updated_at, id);
