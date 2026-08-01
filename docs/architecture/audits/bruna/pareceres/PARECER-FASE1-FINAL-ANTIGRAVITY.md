# Parecer independente — Fase 1, blocos finais (1E/3, 1D/3, 1D/2)

**Crítico:** Antigravity CLI (`agy`), modo `plan`, read-only, esforço alto.
**Data:** 2026-08-01. **Branch:** `develop`.
**Escopo:** commits `1abf58d3` (1E/3) e `e6f6309b` (1D/3) e o trabalho de 1D/2 então não commitado.

> Resposta do executor aos achados, ao final do documento.

---

Parecer técnico referente à auditoria dos dois commits mais recentes e do trabalho não commitado da **Fase 1** da auditoria "Bruna" no repositório `/Users/mateus/Documents/harness-poseidon-backend` (branch `develop`).

---

### Bloco 1: Commit `1abf58d3` — "1E/3: modo de operação do PROJETO governa o despacho"

#### 1. A mudança faz o que afirma fazer? Há caminho residual que a contradiz?
**Sim.** 
- O modo padrão de criação de novos projetos passa a ser `autonomous` em [`Project.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Modules/Harness.Modules.Projects/Domain/Project.cs#L89).
- O `AutoDispatchEnabled` passa a ser `true` por padrão no contrato de configurações em [`AgentRunContracts.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Agents/AgentRunContracts.cs#L68).
- O laço do Chefe em [`ChiefBacklogLoopService.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Agents/ChiefBacklogLoopService.cs#L295) delega a resolução do modo ao [`ProjectOperationModeResolver.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Workflows/ProjectOperationModeResolver.cs#L28), garantindo que a binding de workflow (`workflow_bindings.operation_mode`) tenha precedência sobre o valor legadofixo do projeto (`projects.operation_mode`).
- Se houver falha de infraestrutura ao resolver o modo, a exceção é capturada, logada e o laço executa `continue`, evitando presumi-lo como autônomo.
- Em [`ProjectWorkflowLinker.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Workflows/ProjectWorkflowLinker.cs#L113), o vinculo herda o `projectOperationMode` quando a versão do workflow não especifica `DefaultOperationMode`.

#### 2. Segurança: algum segredo pode vazar? alguma guarda foi enfraquecida?
**Não.**
- Nenhum segredo ou credencial é manipulado ou exposto por essa alteração.
- O `AutonomousActionGuard` (guardião das ações sensíveis durante a execução) permanece completamente intocado.
- Tratamento de exceção fail-safe no laço: falhas na resolução do modo impedem o disparo do ciclo no projeto.

#### 3. Multi-tenant: algum caminho novo cruza fronteira de tenant?
**Não.**
- A chamada [`ProjectOperationModeResolver.ResolveAsync`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Workflows/ProjectOperationModeResolver.cs#L28) recebe obrigatoriamente `profile.TenantId` e repassa para a consulta `ListBindingsAsync(tenantId, projectId, ...)`.

#### 4. Paridade SQLite/Postgres nas mudanças de persistência.
**N/A.** O commit não altera esquemas ou DDL/DML diretamente; utiliza a abstração `IWorkflowCatalogStore` cujas consultas já possuem implementação idêntica em SQLite e Postgres.

#### 5. Os testes provam o que dizem provar, ou passariam trivialmente?
**Provam de forma consistente.**
- [`ProjectOperationModeDispatchTests.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/tests/Harness.IntegrationTests/Agents/ProjectOperationModeDispatchTests.cs#L30) valida no mesmo Host/Tenant a prova positiva (projeto `autonomous` despacha e registra tentativa durável) e a prova negativa (projeto `manual` não despacha e permanece em `ready`).
- Atualização consistente das asserções de testes legados em `ProjectApiTests`, `WorkflowApiTests` e `ProjectTests`.

#### 6. Achados
- **BAIXO**: [`src/Harness.Host/Workflows/ProjectOperationModeResolver.cs:37`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Workflows/ProjectOperationModeResolver.cs#L37) — A resolução assume `bindings[0]`. O sistema impõe restrição de 1 binding por projeto, tornando a premissa válida, mas é recomendável registrar a invariante via docstring/assert.

**STATUS: PASS**

---

### Bloco 2: Commit `e6f6309b` — "1D/3: skill promovida entra no bundle, filtrada por escopo"

#### 1. A mudança faz o que afirma fazer? Há caminho residual que a contradiz?
**Sim.**
- [`PromotedSkillProvider.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Governance/PromotedSkillProvider.cs#L43) lê do repositório de aprendizado as skills com estado `Promoted`.
- O escopo de caminhos (`AllowedScopes`) da persona de origem é resolvido em `ResolveScopesAsync`.
- O [`ContextBundleBuilder.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Modules/Harness.Modules.Governance/Context/ContextBundleBuilder.cs#L316) injeta apenas as fatias de skill (`ContextSkillSlice`) cujos escopos englobam ou coincidem com os caminhos trabalhados na tarefa (`SkillAppliesTo`).
- O casamento em `Covers` é por segmento delimitado por `/`, impedindo correspondências falsas por prefixo de string.

#### 2. Segurança: algum segredo pode vazar? alguma guarda foi enfraquecida?
**Não, reforçado por sanitização.**
- [`PromotedSkillProvider.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Governance/PromotedSkillProvider.cs#L71) verifica `SecretTextProtector.ContainsSecret` nas instruções e no título. Se qualquer segredo for detectado, a skill promovida é descartada integralmente para não propagar credenciais para o contexto de outros agentes.
- Falhas na leitura de skills capturam exceções e logam aviso, permitindo que a execução prossiga em fail-safe sem interromper o fluxo.

#### 3. Multi-tenant: algum caminho novo cruza fronteira de tenant?
**Não.**
- `ListForProjectAsync` repassa `tenantId` e `projectId` para `_candidates.ListAsync`.
- `ResolveScopesAsync` passa `tenantId` e `personaId` para `_agents.GetDefinitionForTenantAsync`.

#### 4. Paridade SQLite/Postgres nas mudanças de persistência.
**N/A.** O provider utiliza a interface de abstração `ILearningCandidateStore`.

#### 5. Os testes provam o que dizem provar, ou passariam trivialmente?
**Provam.**
- [`PromotedSkillProviderTests.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/tests/Harness.IntegrationTests/Governance/PromotedSkillProviderTests.cs#L22) percorre o ciclo real de promoção de uma skill e valida descarte de segredos e aplicação por escopo.
- [`GovernanceRuntimeTests.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/tests/Harness.UnitTests/Governance/GovernanceRuntimeTests.cs#L246) valida o isolamento de escopo por segmento e proibe prefixos falsos como `src/api` casando com `src/apiary/hive.cs`.

#### 6. Achados
- *Nenhum achado.*

**STATUS: PASS**

---

### Bloco 3: Trabalho Não Commitado (`git diff`) — "1D/2: produtividade por assinatura"

#### 1. A mudança faz o que afirma fazer? Há caminho residual que a contradiz?
**Sim.**
- Elimina o viés de cálculo no painel de confiabilidade em [`ReliabilityEndpoints.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Agents/ReliabilityEndpoints.cs#L81). Anteriormente, a medição limitava-se a tarefas com falhas classificadas via MAST. Com a mudança, o endpoint lê as invocações de todo o projeto até o limite de amostra `InvocationSampleSize` (5000).
- Adiciona a visão de uso e custo por assinatura (`SubscriptionUsageContract`) agregando invocações, tarefas tocadas, taxa de sucesso, consumo de tokens e custo em USD.
- Informa `SampleTruncated` quando a quantidade de invocações atinge ou ultrapassa a amostra.
- Expõe a nova funcionalidade na interface React (`frontend/src/features/reliability/**`) e navegação/i18n.

#### 2. Segurança: algum segredo pode vazar? alguma guarda foi enfraquecida?
**Não.**
- O endpoint `/api/v1/projects/{projectId}/reliability` exige autenticação da sessão (`Session(request, profiles, token)`).
- `SubscriptionUsageContract` expõe apenas pseudônimos de contas (`AccountAlias`), consumo de tokens e custos agregados.

#### 3. Multi-tenant: algum caminho novo cruza fronteira de tenant?
**Não.**
- Em [`ReliabilityEndpoints.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Agents/ReliabilityEndpoints.cs#L81): `invocations.GetProjectInvocationsAsync(profile.TenantId, projectId, ...)`.
- As consultas SQL em ambos os drivers SQLite e Postgres possuem a cláusula `WHERE tenant_id = @tenantId AND project_id = @projectId`.

#### 4. Paridade SQLite/Postgres nas mudanças de persistência.
**Paridade Perfeita.**
- [`SqliteModelInvocationStore.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Sqlite/SqliteModelInvocationStore.cs#L90) e [`PostgresModelInvocationStore.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Postgres/PostgresModelInvocationStore.cs#L88) utilizam exatamente a mesma estrutura de query:
  ```sql
  SELECT id, tenant_id, project_id, work_task_id, attempt_id,
         provider, model, account_alias, input_tokens, output_tokens,
         estimated_cost_usd, duration_ms, outcome, invoked_at
  FROM model_invocations
  WHERE tenant_id = @tenantId AND project_id = @projectId
  ORDER BY invoked_at DESC
  LIMIT @limit;
  ```
- O mapeamento dos tipos específicos de dados (`GetDouble` + `Convert.ToDecimal` vs `GetDecimal`) respeita as particularidades de cada driver ADO.NET.

#### 5. Os testes provam o que dizem provar, ou passaria trivialmente?
**Provam.**
- [`ProjectReliabilityApiTests.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/tests/Harness.IntegrationTests/Agents/ProjectReliabilityApiTests.cs#L30) simula invocações para duas assinaturas em tarefas sem nenhuma classificação de falha MAST (`classifiedAttempts == 0`) e verifica que a medição de produtividade/capacidade é calculada corretamente sem depender de falhas prévias.

#### 6. Achados
- **BAIXO**: [`src/Harness.Host/Agents/ReliabilityEndpoints.cs:108`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Agents/ReliabilityEndpoints.cs#L108) — A verificação de sucesso usa `item.Outcome.Contains("success", StringComparison.OrdinalIgnoreCase)`. Embora a coluna de banco não admita nulos, adicionar uma verificação defensiva contra `null` previne potenciais exceções de referência nula em cenários de refatoração futura.

**STATUS: PASS**

---

## Resposta do executor

**Aceito — `ProjectOperationModeResolver:39` (`bindings[0]`).** A invariante existe
(`LinkTemplateAsync` recusa o segundo vínculo com `WorkflowBindingAlreadyExistsException`), mas
estava implícita: quem lesse o código veria "pegar o primeiro de vários" onde na verdade se lê o
único que pode existir. Documentada no ponto de leitura.

**Recusado — `ReliabilityEndpoints` (`Outcome` possivelmente nulo).** `ModelInvocationRecord.Outcome`
é `string` não-anulável e a coluna é `NOT NULL` nos dois bancos. Uma guarda contra `null` ali não
protegeria de nada hoje e afirmaria, para quem lê depois, que o campo é opcional — o oposto do que
o contrato diz. Se um dia o campo passar a ser opcional, quem fizer essa mudança tem o compilador
avisando em todos os pontos de uso; uma guarda preventiva é justamente o que apaga esse aviso.
