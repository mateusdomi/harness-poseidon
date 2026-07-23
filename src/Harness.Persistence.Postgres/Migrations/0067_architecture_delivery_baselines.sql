-- ARC-10: integração Delivery↔Architecture. A arquitetura APROVADA de uma entrega vira a BASELINE
-- ('baseline'); produção atualiza o AS-IS ('as_built'); o encerramento compara proposta × implementação
-- ('closed'). Os dois snapshots (baseline e as-built) são fotos IMUTÁVEIS do modelo (elementos+arestas)
-- e vivem no payload_json (jsonb). Escopo por (tenant_id, project_id). Integra via read-model, sem
-- acoplar forte aos módulos Delivery/Coordination.
CREATE TABLE harness.architecture_baselines
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    status text NOT NULL CHECK (status IN ('baseline','as_built','closed')),
    payload_json jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (tenant_id,id)
);

CREATE INDEX ix_architecture_baselines_scope
    ON harness.architecture_baselines (tenant_id,project_id,id);
