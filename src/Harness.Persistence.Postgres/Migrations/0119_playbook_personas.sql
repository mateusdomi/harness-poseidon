-- Migration 0119 (Fase 2A.3): as nove ESPECIALIDADES do playbook viram personas de verdade.
--
-- Elas existiam como três colunas no `team_specialty_catalog` — chave, nome e uma linha de
-- descrição — e isso é um rótulo, não uma persona. Um agente despachado como "qa" recebia, como
-- toda a sua definição de papel, a frase "Qualidade é cultura; projeta teste desde a Fase 3".
-- Bonita e inútil para decidir o que fazer diante de um card concreto.
--
-- Duas colunas novas em `agent_definitions` respondem a uma pergunta que o catálogo não sabia
-- responder e que a chefe faz a cada card:
--
--   * `activation_criteria_json` — QUANDO acionar esta persona. Sem isso, a escolha de
--     especialista é adivinhação por semelhança de nome, e o card de banco de dados cai no
--     back-end genérico porque "parece parecido".
--   * `non_activation_criteria_json` — quando NÃO acionar. É a fronteira negativa (B11): a
--     ausência dela é a omissão que faz uma persona ser chamada para tudo e não servir para nada.
--
-- As linhas-base entram aqui (id estável, key, name, role, specialty, description) com
-- tenant_id/owner IS NULL, como nas migrações 0018, 0064 e 0075. O conteúdo completo é preenchido
-- de forma idempotente na inicialização pelo BuiltInAgentDefinitionSeeder.

ALTER TABLE harness.agent_definitions ADD COLUMN activation_criteria_json jsonb NOT NULL DEFAULT '[]'::jsonb;
ALTER TABLE harness.agent_definitions ADD COLUMN non_activation_criteria_json jsonb NOT NULL DEFAULT '[]'::jsonb;

INSERT INTO harness.agent_definitions
    (id, agent_key, name, role, specialty, description)
VALUES
    ('01ARZ3NDEKTSV4RRFFQ69G5FBM', 'playbook-product-owner', 'Product Owner', 'specialist', 'Valor de negócio e requisitos',
     'Conduz Triagem e Descoberta: qualifica valor, traduz intenção do cliente em requisitos testáveis e defende o não-objetivo.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBN', 'playbook-arquiteto', 'Arquiteto', 'specialist', 'Arquitetura de solução',
     'Conduz a Arquitetura: escolhe trade-offs conscientes, registra o que cada decisão custa e revisa aderência — não escreve código de produção.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBP', 'playbook-tech-lead', 'Tech Lead', 'specialist', 'Ponte arquitetura e código',
     'Co-conduz o Planejamento e revisa o Desenvolvimento: transforma decisão arquitetural em card executável e usa review como ensino.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBQ', 'playbook-qa', 'QA', 'specialist', 'Qualidade e verificação',
     'Conduz os Testes e instrumenta a Homologação: projeta a verificação desde a Arquitetura, não depois do código pronto.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBR', 'playbook-devops', 'DevOps', 'specialist', 'Entrega e operação de release',
     'Conduz o Release: trata deploy como processo industrial e rollback como parte do plano A.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBS', 'playbook-sre-sustentacao', 'SRE / Sustentação', 'specialist', 'Confiabilidade e sustentação',
     'Conduz a Sustentação: guarda o SLO, conduz incidentes e transforma postmortem em ação com dono e prazo.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBT', 'playbook-security', 'Security', 'specialist', 'Segurança aplicada',
     'Threat model na Arquitetura e verificação ofensiva nos Testes: pensa como atacante e quebra a cadeia de ataque onde dói.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBV', 'playbook-dba-dados', 'DBA / Dados', 'specialist', 'Dados e persistência',
     'Modelo de dados na Arquitetura e desempenho de consulta nas fases seguintes: modela por queries e crescimento reais.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBW', 'playbook-dev-executor', 'Dev Executor', 'specialist', 'Implementação',
     'Constrói no Desenvolvimento, N em paralelo, cada um em worktree isolada com ScopeClaim próprio e dentro do padrão decidido.');
