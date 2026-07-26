-- Migration 0082: o catálogo de templates do playbook (§7) como REGISTROS-SEED — id, nome,
-- fase, tipo de card alvo e campos obrigatórios. O conteúdo integral de cada template é
-- derivado na primeira execução real de projeto (decisão do próprio playbook); aqui vive a
-- ESTRUTURA canônica, consultável pela fábrica. Catálogo global (sem tenant): é dado do
-- produto, não do cliente.
CREATE TABLE harness.workflow_document_templates
(
    code text PRIMARY KEY CHECK (length(code) BETWEEN 2 AND 4),
    name text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    phase text NOT NULL CHECK (length(phase) BETWEEN 1 AND 40),
    target_card_type text NOT NULL CHECK
        (target_card_type IN ('historia','tarefa','bug','spike','adr','documento','revisao','gate','incidente','chamado')),
    required_fields_json jsonb NOT NULL
);

INSERT INTO harness.workflow_document_templates (code,name,phase,target_card_type,required_fields_json) VALUES
('00','Formulário de solicitação inicial','1-Triagem','documento','["titulo","objetivo","conteudo"]'::jsonb),
('01','Ficha de Demanda Qualificada','1-Triagem','documento','["titulo","objetivo","conteudo"]'::jsonb),
('02','Memorando de Recusa','1-Triagem','documento','["titulo","objetivo","conteudo"]'::jsonb),
('02b','Roteiro de entrevista','2-Descoberta','documento','["titulo","objetivo","conteudo"]'::jsonb),
('03','PRD','2-Descoberta','documento','["titulo","objetivo","conteudo"]'::jsonb),
('04','História de usuário','2-Descoberta','historia','["como","quero","para","criterios_gherkin"]'::jsonb),
('05','ADR (MADR)','3-Arquitetura','adr','["contexto","decisao","alternativas","consequencias"]'::jsonb),
('06','SAD','3-Arquitetura','documento','["titulo","objetivo","conteudo"]'::jsonb),
('07','C4 (Contexto+Contêiner)','3-Arquitetura','documento','["titulo","objetivo","conteudo"]'::jsonb),
('08','Comparativo de trade-off','3-Arquitetura','documento','["titulo","objetivo","conteudo"]'::jsonb),
('08b','Sprint backlog','4-Planejamento','documento','["titulo","objetivo","conteudo"]'::jsonb),
('09','Modelo de dados (DER)','3-Arquitetura','documento','["titulo","objetivo","conteudo"]'::jsonb),
('09b','Plano de testes','6-Testes','documento','["titulo","objetivo","conteudo"]'::jsonb),
('10','Roteiro UAT','7-Homologação','documento','["titulo","objetivo","conteudo"]'::jsonb),
('10b','Threat model / pentest','3-Arquitetura','documento','["titulo","objetivo","conteudo"]'::jsonb),
('11','GMUD','8-Release','documento','["titulo","objetivo","conteudo"]'::jsonb),
('11b','Plano de observabilidade','3-Arquitetura','documento','["titulo","objetivo","conteudo"]'::jsonb),
('12','DoR/DoD','4-Planejamento','documento','["titulo","objetivo","conteudo"]'::jsonb),
('12b','Runbook','9-Sustentação','documento','["titulo","objetivo","conteudo"]'::jsonb),
('13','Cronograma de releases','4-Planejamento','documento','["titulo","objetivo","conteudo"]'::jsonb),
('13b','Postmortem','9-Sustentação','documento','["titulo","objetivo","conteudo"]'::jsonb),
('14','Mapa de riscos/dependências','4-Planejamento','documento','["titulo","objetivo","conteudo"]'::jsonb),
('15','Code review estruturado','5-Desenvolvimento','revisao','["escopo","achados","veredito"]'::jsonb),
('16','Métricas DORA','5-Desenvolvimento','documento','["titulo","objetivo","conteudo"]'::jsonb),
('17','Dicionário ubíquo','5-Desenvolvimento','documento','["titulo","objetivo","conteudo"]'::jsonb),
('18','Briefing técnico','5-Desenvolvimento','documento','["titulo","objetivo","conteudo"]'::jsonb),
('19','Análise de incidente dev','5-Desenvolvimento','documento','["titulo","objetivo","conteudo"]'::jsonb),
('20','Relatório de quality gate','6-Testes','documento','["titulo","objetivo","conteudo"]'::jsonb),
('21','Relatório de performance','6-Testes','documento','["titulo","objetivo","conteudo"]'::jsonb),
('22','Relatório de pentest','6-Testes','documento','["titulo","objetivo","conteudo"]'::jsonb),
('23','Parecer Go/No-Go','6-Testes','documento','["titulo","objetivo","conteudo"]'::jsonb),
('24','Resultados UAT','7-Homologação','documento','["titulo","objetivo","conteudo"]'::jsonb),
('25','Termo de Aceite','7-Homologação','gate','["criterios","aprovador","veredito"]'::jsonb),
('26','Defeitos UAT','7-Homologação','documento','["titulo","objetivo","conteudo"]'::jsonb),
('27','Notas de versão','8-Release','documento','["titulo","objetivo","conteudo"]'::jsonb),
('28','Plano de rollback','8-Release','documento','["titulo","objetivo","conteudo"]'::jsonb),
('29','SBOM','8-Release','documento','["titulo","objetivo","conteudo"]'::jsonb),
('30','Relatório mensal de operação','9-Sustentação','documento','["titulo","objetivo","conteudo"]'::jsonb),
('31','Capacity planning','9-Sustentação','tarefa','["objetivo","validacao_objetiva"]'::jsonb),
('32','Game day report','9-Sustentação','tarefa','["objetivo","validacao_objetiva"]'::jsonb),
('33','DR drill report','9-Sustentação','tarefa','["objetivo","validacao_objetiva"]'::jsonb),
('34','Comunicado de aprovação','7-Homologação','documento','["titulo","objetivo","conteudo"]'::jsonb),
('34b','Story map','2-Descoberta','documento','["titulo","objetivo","conteudo"]'::jsonb),
('35','Comunicado de sustentação','9-Sustentação','documento','["titulo","objetivo","conteudo"]'::jsonb),
('35b','NFRs preliminares','2-Descoberta','documento','["titulo","objetivo","conteudo"]'::jsonb),
('36','Card história completa','2-Descoberta','historia','["como","quero","para","criterios_gherkin"]'::jsonb),
('37','Card tarefa leve','4-Planejamento','tarefa','["objetivo","validacao_objetiva"]'::jsonb);
