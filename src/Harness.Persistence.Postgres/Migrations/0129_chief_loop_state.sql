-- Migration 0129: estado durável do laço da chefe (Onda 0.6).
-- Ver a migration SQLite homônima para o racional.
CREATE TABLE harness.chief_loop_state
(
    tenant_id char(26) NOT NULL,
    kind varchar(20) NOT NULL CHECK (kind IN ('dispatch_backoff', 'review_backoff', 'no_progress')),
    entry_id char(26) NOT NULL,
    not_before timestamptz NULL,
    counter integer NOT NULL DEFAULT 0 CHECK (counter >= 0),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, kind, entry_id)
);
ALTER TABLE harness.chief_loop_state ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.chief_loop_state FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON harness.chief_loop_state
    USING (tenant_id = current_setting('app.tenant_id', true));
