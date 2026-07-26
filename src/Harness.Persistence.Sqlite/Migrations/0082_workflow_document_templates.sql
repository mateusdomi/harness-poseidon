-- Migration 0082: o catálogo de templates do playbook (§7) como REGISTROS-SEED — id, nome,
-- fase, tipo de card alvo e campos obrigatórios. O conteúdo integral de cada template é
-- derivado na primeira execução real de projeto (decisão do próprio playbook); aqui vive a
-- ESTRUTURA canônica, consultável pela fábrica. Catálogo global (sem tenant): é dado do
-- produto, não do cliente.
CREATE TABLE workflow_document_templates
(
    code TEXT PRIMARY KEY CHECK (length(code) BETWEEN 2 AND 4),
    name TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    phase TEXT NOT NULL CHECK (length(phase) BETWEEN 1 AND 40),
    target_card_type TEXT NOT NULL CHECK
        (target_card_type IN ('historia','tarefa','bug','spike','adr','documento','revisao','gate','incidente','chamado')),
    required_fields_json TEXT NOT NULL
);

INSERT INTO workflow_document_templates (code,name,phase,target_card_type,required_fields_json) VALUES
('00','Formulário de solicitação inicial','1-Triagem','documento','["titulo","objetivo","conteudo"]'),
('01','Ficha de Demanda Qualificada','1-Triagem','documento','["titulo","objetivo","conteudo"]'),
('02','Memorando de Recusa','1-Triagem','documento','["titulo","objetivo","conteudo"]'),
('02b','Roteiro de entrevista','2-Descoberta','documento','["titulo","objetivo","conteudo"]'),
('03','PRD','2-Descoberta','documento','["titulo","objetivo","conteudo"]'),
('04','História de usuário','2-Descoberta','historia','["como","quero","para","criterios_gherkin"]'),
('05','ADR (MADR)','3-Arquitetura','adr','["contexto","decisao","alternativas","consequencias"]'),
('06','SAD','3-Arquitetura','documento','["titulo","objetivo","conteudo"]'),
('07','C4 (Contexto+Contêiner)','3-Arquitetura','documento','["titulo","objetivo","conteudo"]'),
('08','Comparativo de trade-off','3-Arquitetura','documento','["titulo","objetivo","conteudo"]'),
('08b','Sprint backlog','4-Planejamento','documento','["titulo","objetivo","conteudo"]'),
('09','Modelo de dados (DER)','3-Arquitetura','documento','["titulo","objetivo","conteudo"]'),
('09b','Plano de testes','6-Testes','documento','["titulo","objetivo","conteudo"]'),
('10','Roteiro UAT','7-Homologação','documento','["titulo","objetivo","conteudo"]'),
('10b','Threat model / pentest','3-Arquitetura','documento','["titulo","objetivo","conteudo"]'),
('11','GMUD','8-Release','documento','["titulo","objetivo","conteudo"]'),
('11b','Plano de observabilidade','3-Arquitetura','documento','["titulo","objetivo","conteudo"]'),
('12','DoR/DoD','4-Planejamento','documento','["titulo","objetivo","conteudo"]'),
('12b','Runbook','9-Sustentação','documento','["titulo","objetivo","conteudo"]'),
('13','Cronograma de releases','4-Planejamento','documento','["titulo","objetivo","conteudo"]'),
('13b','Postmortem','9-Sustentação','documento','["titulo","objetivo","conteudo"]'),
('14','Mapa de riscos/dependências','4-Planejamento','documento','["titulo","objetivo","conteudo"]'),
('15','Code review estruturado','5-Desenvolvimento','revisao','["escopo","achados","veredito"]'),
('16','Métricas DORA','5-Desenvolvimento','documento','["titulo","objetivo","conteudo"]'),
('17','Dicionário ubíquo','5-Desenvolvimento','documento','["titulo","objetivo","conteudo"]'),
('18','Briefing técnico','5-Desenvolvimento','documento','["titulo","objetivo","conteudo"]'),
('19','Análise de incidente dev','5-Desenvolvimento','documento','["titulo","objetivo","conteudo"]'),
('20','Relatório de quality gate','6-Testes','documento','["titulo","objetivo","conteudo"]'),
('21','Relatório de performance','6-Testes','documento','["titulo","objetivo","conteudo"]'),
('22','Relatório de pentest','6-Testes','documento','["titulo","objetivo","conteudo"]'),
('23','Parecer Go/No-Go','6-Testes','documento','["titulo","objetivo","conteudo"]'),
('24','Resultados UAT','7-Homologação','documento','["titulo","objetivo","conteudo"]'),
('25','Termo de Aceite','7-Homologação','gate','["criterios","aprovador","veredito"]'),
('26','Defeitos UAT','7-Homologação','documento','["titulo","objetivo","conteudo"]'),
('27','Notas de versão','8-Release','documento','["titulo","objetivo","conteudo"]'),
('28','Plano de rollback','8-Release','documento','["titulo","objetivo","conteudo"]'),
('29','SBOM','8-Release','documento','["titulo","objetivo","conteudo"]'),
('30','Relatório mensal de operação','9-Sustentação','documento','["titulo","objetivo","conteudo"]'),
('31','Capacity planning','9-Sustentação','tarefa','["objetivo","validacao_objetiva"]'),
('32','Game day report','9-Sustentação','tarefa','["objetivo","validacao_objetiva"]'),
('33','DR drill report','9-Sustentação','tarefa','["objetivo","validacao_objetiva"]'),
('34','Comunicado de aprovação','7-Homologação','documento','["titulo","objetivo","conteudo"]'),
('34b','Story map','2-Descoberta','documento','["titulo","objetivo","conteudo"]'),
('35','Comunicado de sustentação','9-Sustentação','documento','["titulo","objetivo","conteudo"]'),
('35b','NFRs preliminares','2-Descoberta','documento','["titulo","objetivo","conteudo"]'),
('36','Card história completa','2-Descoberta','historia','["como","quero","para","criterios_gherkin"]'),
('37','Card tarefa leve','4-Planejamento','tarefa','["objetivo","validacao_objetiva"]');
