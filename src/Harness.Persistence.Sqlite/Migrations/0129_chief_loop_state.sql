-- Migration 0129: estado durável do laço da chefe (Onda 0.6).
--
-- Backoff de despacho, backoff de revisão e contagem de não-progresso viviam só em memória: um
-- reinício do Host devolvia todos os cards ao despacho imediato e zerava o freio — justamente
-- depois de uma queda, que é quando a fábrica mais tende a repetir trabalho. A regra que esta
-- tabela materializa: TODO estado que explica uma decisão de loop sobrevive ao processo que
-- executa o loop.
--
-- Chave-valor deliberadamente simples: `kind` fecha o vocabulário, `entry_id` é o card/attempt,
-- `not_before` serve aos backoffs e `counter` ao não-progresso. Upsert idempotente.
CREATE TABLE chief_loop_state
(
    tenant_id TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('dispatch_backoff', 'review_backoff', 'no_progress')),
    entry_id TEXT NOT NULL,
    not_before TEXT NULL,
    counter INTEGER NOT NULL DEFAULT 0 CHECK (counter >= 0),
    updated_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, kind, entry_id)
);
