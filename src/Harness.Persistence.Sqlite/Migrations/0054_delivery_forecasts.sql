-- DEL-09: histórico APPEND-ONLY de previsões honestas por entrega (projeto). Cada previsão é uma
-- linha nova (nunca update/delete), tornando o "histórico de previsão" auditável e imune a
-- sobrescrita silenciosa (DEL-02). A identidade da entrega deriva do projeto — não duplicamos dados
-- de Coordination/Documents/Governance; a Central de Entregas é um conjunto de read-models sobre eles.
-- forecast_date é NULO quando a evidência é insuficiente: o forecaster NUNCA inventa uma data.
CREATE TABLE delivery_forecasts
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    forecast_date TEXT NULL,
    confidence TEXT NOT NULL CHECK (confidence IN ('low','medium','high')),
    confidence_percent INTEGER NOT NULL CHECK (confidence_percent BETWEEN 0 AND 100),
    has_sufficient_evidence INTEGER NOT NULL CHECK (has_sufficient_evidence IN (0,1)),
    basis_json TEXT NOT NULL CHECK (json_valid(basis_json) AND json_type(basis_json) = 'array'),
    created_at TEXT NOT NULL,
    UNIQUE (tenant_id,id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES projects(tenant_id,id)
);

CREATE INDEX ix_delivery_forecasts_scope
    ON delivery_forecasts (tenant_id,project_id,created_at,id);
