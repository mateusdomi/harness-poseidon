-- Migration 0135: adiciona o tipo de card 'objetivo' ao vocabulário do work_tasks.
-- Perfil v2 (Understand → Build → Prove): um objetivo funcional inteiro por card, entregue
-- por um executor persistente dono do repositório do produto.
DO $$
DECLARE
    constraint_name text;
BEGIN
    SELECT conname INTO constraint_name
    FROM pg_constraint
    WHERE conrelid = 'harness.work_tasks'::regclass
      AND contype = 'c'
      AND pg_get_constraintdef(oid) LIKE '%card_type%';
    IF constraint_name IS NOT NULL THEN
        EXECUTE format('ALTER TABLE harness.work_tasks DROP CONSTRAINT %I', constraint_name);
    END IF;
END $$;

ALTER TABLE harness.work_tasks ADD CONSTRAINT work_tasks_card_type_check
    CHECK (card_type IN (
        'feature','agent_task','human_gate','spike','decision',
        'historia','tarefa','bug','adr','documento',
        'revisao','gate','incidente','chamado','council','objetivo'));
