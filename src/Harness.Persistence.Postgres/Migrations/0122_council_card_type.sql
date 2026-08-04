-- Migration 0122: adiciona o tipo de card 'council' ao vocabulário do work_tasks.
-- F-03 — os pareceres do Conselho precisam de uma marca própria para que a regra de
-- revisão independente possa isentá-los: o parecer é, por construção, uma opinião
-- crítica sobre trabalho de terceiro, e exigir segundo par de olhos cria uma regressão
-- infinita que consome o próprio elenco de críticos.
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
        'revisao','gate','incidente','chamado','council'));
