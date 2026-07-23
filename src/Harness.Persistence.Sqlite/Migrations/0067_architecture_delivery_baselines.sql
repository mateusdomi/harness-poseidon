-- ARC-10: integração Delivery↔Architecture. A arquitetura APROVADA de uma entrega vira a BASELINE
-- ('baseline'); produção atualiza o AS-IS ('as_built'); o encerramento compara proposta × implementação
-- ('closed'). Os dois snapshots (baseline e as-built) são fotos IMUTÁVEIS do modelo (elementos+arestas)
-- e vivem no payload_json. Escopo por (tenant_id, project_id). Integra via read-model, sem acoplar forte
-- aos módulos Delivery/Coordination.
CREATE TABLE architecture_baselines
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('baseline','as_built','closed')),
    payload_json TEXT NOT NULL CHECK (json_valid(payload_json)),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id,id)
);

CREATE INDEX ix_architecture_baselines_scope
    ON architecture_baselines (tenant_id,project_id,id);
