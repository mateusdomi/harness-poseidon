-- DEL-09: histórico APPEND-ONLY de previsões honestas por entrega (projeto). Cada previsão é uma
-- linha nova (nunca update/delete), tornando o "histórico de previsão" auditável e imune a
-- sobrescrita silenciosa (DEL-02). A identidade da entrega deriva do projeto — não duplicamos dados
-- de Coordination/Documents/Governance; a Central de Entregas é um conjunto de read-models sobre eles.
-- forecast_date é NULO quando a evidência é insuficiente: o forecaster NUNCA inventa uma data.
CREATE TABLE harness.delivery_forecasts
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    forecast_date timestamptz NULL,
    confidence text NOT NULL CHECK (confidence IN ('low','medium','high')),
    confidence_percent integer NOT NULL CHECK (confidence_percent BETWEEN 0 AND 100),
    has_sufficient_evidence boolean NOT NULL,
    basis_json jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    UNIQUE (tenant_id,id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES harness.projects(tenant_id,id),
    CHECK (jsonb_typeof(basis_json) = 'array')
);

CREATE INDEX ix_delivery_forecasts_scope
    ON harness.delivery_forecasts (tenant_id,project_id,created_at,id);
