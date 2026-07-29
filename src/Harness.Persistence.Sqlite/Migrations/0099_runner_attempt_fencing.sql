-- AUTORIDADE POR IDENTIDADE, NÃO POR PROCESSO (B5).
--
-- `runner_attempts` guardava `runner_id` e o usava para decidir posse: mensagem cujo runner_id
-- diferisse do registrado era recusada como "dono diferente". O efeito prático era o oposto do
-- pretendido — um agente que REINICIAVA ganhava identificador novo e tinha a própria conclusão
-- jogada fora, com o trabalho já feito e o relatório pronto.
--
-- O que separa o agente reiniciado de um terceiro não é o identificador do processo: é o fencing
-- token, que só o despacho emite. Fencing igual = mesma tentativa, venha do processo que vier.
-- Fencing menor = resultado tardio de tentativa superada, que não pode sobrescrever a vigente.
--
-- Zero significa "tentativa anterior ao B5": nessas, o fencing não é verificado, porque recusar
-- mensagem legítima de um runner em voo é pior do que aceitar sem a prova extra.
ALTER TABLE runner_attempts ADD COLUMN fencing_token INTEGER NOT NULL DEFAULT 0
    CHECK (fencing_token >= 0);
