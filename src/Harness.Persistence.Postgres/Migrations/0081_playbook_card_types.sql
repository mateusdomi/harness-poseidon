-- Migration 0081: amplia o vocabulário de card_type com os tipos canônicos do playbook (§3).
-- Os cinco tipos pré-playbook permanecem válidos: dados existentes e rotas atuais não quebram.
-- O CHECK original nasceu inline na 0049 (nome auto-gerado), então o drop resolve o nome
-- dinamicamente em vez de apostar na convenção.
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
        'revisao','gate','incidente','chamado'));
