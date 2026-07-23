-- DEL-04/DEL-05/DEL-10: relatórios de entrega. Cada relatório é um SNAPSHOT versionado cujo CONTEÚDO
-- (content + data_snapshot) é IMUTÁVEL; só as colunas de ciclo de vida avançam (draft → approved →
-- sent). A foto (data_snapshot) deriva do Projeto 360/métricas/previsão — não duplicamos dados de
-- Coordination/Documents/Governance. content é NULO quando o formato pedido ainda não tem renderizador
-- (pdf/pptx/word/zip → 'format_not_available'), mas a foto permanece para uma renderização futura.
CREATE TABLE harness.delivery_reports
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    type text NOT NULL CHECK (type IN
        ('weekly_executive_status','milestone_report','homologation_readiness',
         'production_readiness','closure_dossier')),
    format text NOT NULL CHECK (format IN
        ('markdown','html','csv','json','pdf','pptx','word','zip')),
    status text NOT NULL CHECK (status IN ('draft','approved','sent')),
    audience text NOT NULL,
    classification text NOT NULL,
    version integer NOT NULL CHECK (version >= 1),
    content_type text NOT NULL,
    content text NULL,
    data_snapshot jsonb NOT NULL,
    approved_by text NULL,
    approved_at timestamptz NULL,
    sent_at timestamptz NULL,
    created_at timestamptz NOT NULL,
    UNIQUE (tenant_id,id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES harness.projects(tenant_id,id)
);

CREATE INDEX ix_delivery_reports_scope
    ON harness.delivery_reports (tenant_id,project_id,created_at,id);

CREATE INDEX ix_delivery_reports_type
    ON harness.delivery_reports (tenant_id,project_id,type);

-- Auditoria APPEND-ONLY de envios (DEL-10): registra QUEM/QUANDO/VERSÃO/CANAL. O destinatário é a
-- REFERÊNCIA OPACA (`env://`, `secret://`, `keychain://`) — nunca um endereço literal nem segredo.
CREATE TABLE harness.delivery_report_sends
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    report_id char(26) NOT NULL,
    version integer NOT NULL CHECK (version >= 1),
    channel text NOT NULL,
    recipient_reference text NOT NULL,
    sent_by text NOT NULL,
    result text NOT NULL,
    sent_at timestamptz NOT NULL,
    UNIQUE (tenant_id,id),
    FOREIGN KEY (tenant_id,report_id) REFERENCES harness.delivery_reports(tenant_id,id)
);

CREATE INDEX ix_delivery_report_sends_scope
    ON harness.delivery_report_sends (tenant_id,report_id,sent_at,id);
