-- DEL-04/DEL-05/DEL-10: relatórios de entrega. Cada relatório é um SNAPSHOT versionado cujo CONTEÚDO
-- (content + data_snapshot) é IMUTÁVEL; só as colunas de ciclo de vida avançam (draft → approved →
-- sent). A foto (data_snapshot) deriva do Projeto 360/métricas/previsão — não duplicamos dados de
-- Coordination/Documents/Governance. content é NULO quando o formato pedido ainda não tem renderizador
-- (pdf/pptx/word/zip → 'format_not_available'), mas a foto permanece para uma renderização futura.
CREATE TABLE delivery_reports
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    type TEXT NOT NULL CHECK (type IN
        ('weekly_executive_status','milestone_report','homologation_readiness',
         'production_readiness','closure_dossier')),
    format TEXT NOT NULL CHECK (format IN
        ('markdown','html','csv','json','pdf','pptx','word','zip')),
    status TEXT NOT NULL CHECK (status IN ('draft','approved','sent')),
    audience TEXT NOT NULL,
    classification TEXT NOT NULL,
    version INTEGER NOT NULL CHECK (version >= 1),
    content_type TEXT NOT NULL,
    content TEXT NULL,
    data_snapshot TEXT NOT NULL CHECK (json_valid(data_snapshot)),
    approved_by TEXT NULL,
    approved_at TEXT NULL,
    sent_at TEXT NULL,
    created_at TEXT NOT NULL,
    UNIQUE (tenant_id,id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES projects(tenant_id,id)
);

CREATE INDEX ix_delivery_reports_scope
    ON delivery_reports (tenant_id,project_id,created_at,id);

CREATE INDEX ix_delivery_reports_type
    ON delivery_reports (tenant_id,project_id,type);

-- Auditoria APPEND-ONLY de envios (DEL-10): registra QUEM/QUANDO/VERSÃO/CANAL. O destinatário é a
-- REFERÊNCIA OPACA (`env://`, `secret://`, `keychain://`) — nunca um endereço literal nem segredo.
CREATE TABLE delivery_report_sends
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    report_id TEXT NOT NULL,
    version INTEGER NOT NULL CHECK (version >= 1),
    channel TEXT NOT NULL,
    recipient_reference TEXT NOT NULL,
    sent_by TEXT NOT NULL,
    result TEXT NOT NULL,
    sent_at TEXT NOT NULL,
    UNIQUE (tenant_id,id),
    FOREIGN KEY (tenant_id,report_id) REFERENCES delivery_reports(tenant_id,id)
);

CREATE INDEX ix_delivery_report_sends_scope
    ON delivery_report_sends (tenant_id,report_id,sent_at,id);
