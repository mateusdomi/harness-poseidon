# Parecer independente — Fase 2, blocos 2A e 2B

**Crítico:** Antigravity CLI (`agy`), modo `plan`, read-only, esforço alto.
**Data:** 2026-08-01. **Branch:** `develop`. **Commit auditado:** `d092e645`.

> Resposta do executor ao final.

---

### PARECER TÉCNICO DE AUDITORIA — FASE 2 (COMMIT `d092e645`)

---

### BLOCO 2A — PROFUNDIDADE COMPORTAMENTAL E ENFORCEMENT DE TEMPLATES / PERSONAS

#### 1. A mudança faz o que afirma? Há caminho residual que a contradiz?
* **Parcialmente.** 
  * A verificação de compliance de templates (`DocumentTemplateCompliance.Check`) foi integrada em [`src/Harness.Host/Documents/DocumentEndpoints.cs:117`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Documents/DocumentEndpoints.cs#L117) durante a **criação** (`CreateDocumentAsync`).
  * **Caminho Residual Identificado:** A verificação de compliance **não foi adicionada** nos endpoints de criação/salvamento de novas versões de documentos em [`DocumentEndpoints.cs:190-240`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Documents/DocumentEndpoints.cs#L190-L240) (`CreateVersionAsync` / `SaveDocumentVersionAsync` / `CreateVersionCoreAsync`). Um documento criado em conformidade pode ter uma versão posterior enviada sem os campos exigidos ou fora de ordem, burlando a trava do template no acervo.
  * A injeção da Persona no bundle de contexto via `ContextPersonaSlice` em [`ContextBundleBuilder.cs:334`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Modules/Harness.Modules.Governance/Context/ContextBundleBuilder.cs#L334) marca o segmento como obrigatório (`IsRequired = true`), garantindo que não seja podado pelo orçamento de tokens.

#### 2. Paridade SQLite/Postgres das migrations 0118/0119
* **Conforme.**
  * **0118 (`workflow_document_templates`):** Coluna `metric_formats_json` criada como `TEXT NOT NULL DEFAULT '{}'` (SQLite em [`0118_template_real_fields.sql:6`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Sqlite/Migrations/0118_template_real_fields.sql#L6)) e `jsonb NOT NULL DEFAULT '{}'::jsonb` (Postgres em [`0118_template_real_fields.sql:6`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Postgres/Migrations/0118_template_real_fields.sql#L6)). Coluna `guidance` criada como `TEXT`/`text`.
  * **0119 (`agent_definitions`):** Colunas `activation_criteria_json` e `non_activation_criteria_json` seguem a mesma conversão de tipos `TEXT` / `jsonb` com defaults adequados em ambos os bancos.
  * Ambas as tabelas armazenam as definições padrão (built-in seed) com `tenant_id IS NULL`, permitindo resolução global compartilhada.

#### 3. Multi-tenant
* **Conforme.** 
  * As buscas de personas em [`AgentRunOrchestrator.cs:1061`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Agents/AgentRunOrchestrator.cs#L1061) utilizam `ListDefinitionsForTenantAsync(command.TenantId, ...)`, que isola definições por tenant ou recorre aos registros globais (`tenant_id IS NULL`).

#### 4. Segurança
* **Conforme.** 
  * O segmento `ContextPersonaSlice` renderiza apenas atributos públicos e conceituais do agente (`Key`, `Name`, `Mindset`, `Mission`, `OperatingPrinciples`, `Deliverables`, `Limitations`). Não há vazamento de credenciais ou chaves.

#### 5. Avaliação dos Testes
* **Parcialmente aderentes.**
  * `StandardWorkflowDocParityTests` valida com sucesso a paridade literal entre o arquivo Markdown [`standard-workflow.md`](file:///Users/mateus/Documents/harness-poseidon-backend/docs/architecture/workflows/standard-workflow.md) e a esteira materializada em C#.
  * `DocumentTemplateComplianceTests` cobre normalização, ausência e ordenação de seções. Contudo, não há teste de integração garantindo que atualizações de versão de documento validem compliance.

#### 6. Achados do Bloco 2A
* **[MÉDIO]** `src/Harness.Host/Documents/DocumentEndpoints.cs:204`: Endpoints de atualização de versão (`CreateVersionCoreAsync`) não invocam `DocumentTemplateCompliance.Check`, permitindo alteração de documento para um estado não-conforme após a criação.

**STATUS BLOCO 2A: FAIL** (devido ao caminho residual em `DocumentEndpoints.cs` que permite burlar a governança de templates na edição de versões).

---

### BLOCO 2B — DESPACHO DETERMINÍSTICO DE INTENÇÃO (B14) E MEDIÇÃO

#### 1. A mudança faz o que afirma? Há caminho residual que a contradiz?
* **Sim.**
  * O portão determinístico `ChiefIntentGate.Apply(output)` é aplicado em [`ChiefTurnBackgroundService.cs:433`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Workers/ChiefTurnBackgroundService.cs#L433).
  * A variável local `output` é explicitamente substituída pelo resultado filtrado (`output = gated.Output;`) **antes** que qualquer demanda ou ação de equipe seja convertida em registros/seeds para o board em [`ChiefTurnBackgroundService.cs:473`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Workers/ChiefTurnBackgroundService.cs#L473).

#### 2. Paridade SQLite/Postgres da migration 0120
* **Conforme.**
  * **SQLite (`chief_turn_intents`):** [`0120_chief_turn_intents.sql:16`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Sqlite/Migrations/0120_chief_turn_intents.sql#L16) cria tabela com tipos `TEXT`, `REAL`, `INTEGER`.
  * **Postgres (`chief_turn_intents`):** [`0120_chief_turn_intents.sql:16`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Postgres/Migrations/0120_chief_turn_intents.sql#L16) usa `text`, `double precision`, `integer`, `bigint`, `timestamptz`.
  * **RLS:** A migration Postgres habilita e força Row Level Security explicitamente com política baseada em `current_setting('poseidon.tenant_id', true)` ([`0120_chief_turn_intents.sql:38-42`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Postgres/Migrations/0120_chief_turn_intents.sql#L38-L42)).
  * Constraints de tamanho de intenção, confiança entre `0` e `1` e contadores não-negativos são equivalentes em ambos os bancos.

#### 3. Multi-tenant
* **Conforme.**
  * A tabela `chief_turn_intents` exige `tenant_id` na chave primária e na FK para `projects`.
  * O repositório (`PostgresChiefTurnIntentStore` / `SqliteChiefTurnIntentStore`) obriga a passagem de `tenant_id` em todas as operações de gravação e consulta.
  * No Postgres, o RLS impede vazamento entre tenants mesmo em consultas acidentais sem `WHERE tenant_id`.

#### 4. Segurança
* **Conforme.**
  * O mecanismo impede que o modelo execute ações arbitrárias na infraestrutura do projeto quando a intenção classificada não prevê tais privilégios. Fallbacks de baixa confiança ou intenções desconhecidas forçam `unmatched` (sem permissão de ação).

#### 5. Avaliação dos Testes
* **Efetivos.**
  * `ChiefIntentDispatchTests` em [`ChiefIntentDispatchTests.cs:1-222`](file:///Users/mateus/Documents/harness-poseidon-backend/tests/Harness.UnitTests/Agents/ChiefIntentDispatchTests.cs#L1) testa cenários adversariais reais:
    * Valida que turnos `conversa_geral` que emitem demandas têm suas demandas zeradas e contabilizadas em `DemandsDropped`.
    * Valida recusa por baixa confiança (< 0,6) caindo para `unmatched`.
    * Valida preserção da resposta textual enquanto remove ações não autorizadas.

#### 6. O portão de intenção é inescapável? Existe caminho em que demanda de um turno `conversa_geral` chegue ao board?
* **Inescapável.**
  * Não existe caminho residual. Em [`ChiefTurnBackgroundService.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Workers/ChiefTurnBackgroundService.cs#L433), `ChiefIntentGate.Apply(output)` filtra o objeto `output`. Se a intenção for `conversa_geral`, a flag `AllowedActions` é `ChiefTurnAction.None`, resultando em `Demands = []`. O loop subsequente que criaria as demandas consome uma lista vazia.

#### 7. Achados do Bloco 2B
* Nenhum achado crítico, alto, médio ou baixo identificado no Bloco 2B.

**STATUS BLOCO 2B: PASS**

---

## Resposta do executor

**Achado ACEITO e corrigido — `DocumentEndpoints.cs`: a conformidade só valia na criação.** O
crítico está certo, e o achado é o mais importante da rodada: validar apenas a criação deixava
aberto o caminho óbvio de criar conforme e, na versão seguinte, gravar qualquer coisa. O gate
valeria para o primeiro instante da vida do documento e para mais nenhum — que é o mesmo que não
valer, porque o conteúdo que importa é o vigente, não o inicial.

A correção não foi apenas "chamar o validador também na edição". O template passou a ser
**propriedade do documento** (migration 0121, coluna `template_code` nos dois bancos): a
verificação da nova versão lê o template do documento, não do pedido. Passar o código no pedido de
edição deixaria quem edita trocá-lo por um template mais frouxo — trocaria um buraco por outro,
menos visível.

Teste que prova o fechamento em `PlaybookTemplateCatalogTests`: cria-se uma GMUD conforme, tenta-se
degradá-la para um corpo sem o plano de rollback testado, e a edição é recusada nomeando a seção
que falta.

**STATUS 2B: PASS**, sem achados — incluindo a verificação de que o portão de intenção é
inescapável no caminho do worker.
