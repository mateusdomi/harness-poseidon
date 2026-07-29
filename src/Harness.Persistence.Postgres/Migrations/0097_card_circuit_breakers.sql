-- Paridade com o SQLite: o circuito por card abre com falhas consecutivas e só fecha por
-- replanejamento da Bruna — tempo não corrige enunciado errado.
CREATE TABLE harness.card_circuit_breakers
(
    tenant_id text NOT NULL,
    task_id text NOT NULL,
    project_id text NOT NULL,
    state text NOT NULL DEFAULT 'closed' CHECK (state IN ('closed','open')),
    consecutive_failures integer NOT NULL DEFAULT 0 CHECK (consecutive_failures >= 0),
    last_failure_reason_code text NULL
        CHECK (last_failure_reason_code IS NULL OR length(last_failure_reason_code) <= 200),
    last_failure_at timestamptz NULL,
    opened_at timestamptz NULL,
    replanned_at timestamptz NULL,
    replan_note text NULL CHECK (replan_note IS NULL OR length(replan_note) <= 2000),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, task_id),
    CHECK ((state = 'open') = (opened_at IS NOT NULL))
);

CREATE INDEX ix_card_circuit_breakers_open
    ON harness.card_circuit_breakers (tenant_id, project_id, opened_at)
    WHERE state = 'open';

ALTER TABLE harness.card_circuit_breakers ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.card_circuit_breakers FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON harness.card_circuit_breakers
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));
