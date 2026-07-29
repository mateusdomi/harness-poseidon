-- Paridade com o SQLite: quem decide a posse da tentativa é o fencing do despacho, não o
-- identificador do processo — senão o agente que reinicia perde o próprio trabalho.
ALTER TABLE harness.runner_attempts
    ADD COLUMN fencing_token bigint NOT NULL DEFAULT 0
    CHECK (fencing_token >= 0);
