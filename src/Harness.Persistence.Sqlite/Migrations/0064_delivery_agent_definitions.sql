-- DEL-08: definições canônicas built-in das personas de Delivery ("sob demanda"). Inserimos APENAS as
-- linhas-base (id estável, key, name, role, specialty, description) com tenant_id/owner IS NULL, do
-- mesmo modo que a migração inicial do catálogo (0018) fez com as personas de sistema. O conteúdo
-- completo (persona, missão, princípios, ~14 campos) é preenchido de forma idempotente na
-- inicialização pelo BuiltInAgentDefinitionSeeder (guarda owner IS NULL). São definições de catálogo,
-- não agentes sempre-ligados: nenhuma linha em `agents` é criada aqui.
INSERT INTO agent_definitions
    (id, agent_key, name, role, specialty, description)
VALUES
    ('01ARZ3NDEKTSV4RRFFQ69G5FB1', 'delivery-tech-lead-copilot', 'Tech Lead Copilot', 'specialist', 'Delivery orchestration and tech leadership',
     'Assists the Tech Lead across the delivery: reads the 360, surfaces what needs attention, and frames the next decision.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FB2', 'delivery-daily-intelligence', 'Daily Intelligence', 'specialist', 'Daily briefings and standup intelligence',
     'Prepares the pre-daily briefing, captures typed markings during the daily, and composes the post-daily summary.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FB3', 'delivery-risk-dependency-analyst', 'Risk & Dependency Analyst', 'specialist', 'Risk and dependency analysis',
     'Identifies risks, blockers, and unresolved dependencies from recorded delivery signals and proposes mitigations.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FB4', 'delivery-forecast-analyst', 'Delivery Forecast', 'specialist', 'Honest delivery forecasting',
     'Produces the honest forecast: never invents a date, explains the basis, and tracks forecast accuracy over time.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FB5', 'delivery-quality-release-auditor', 'Quality & Release Auditor', 'specialist', 'Quality gates and release readiness',
     'Audits homologation and production readiness against recorded evidence and blocks release when the gates are not met.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FB6', 'delivery-documentation-steward', 'Documentation Steward', 'specialist', 'Delivery documentation stewardship',
     'Keeps the mandatory delivery documentation complete, current, and traceable to the change it describes.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FB7', 'delivery-executive-reporting', 'Executive Reporting', 'specialist', 'Executive delivery reporting',
     'Turns the delivery 360 into clear executive status and milestone reports, free of fabricated numbers.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FB8', 'delivery-benefits-analyst', 'Benefits Analyst', 'specialist', 'Planned versus realized benefits',
     'Compares planned against realized value from recorded plans and outcomes to inform the benefits review.');
