AUDITORIA RETROATIVA — FASE 0 (0A3, 0B1, 0B2, 0C)

## 0A3 — sanitização
Arquivos revisados:
- [`src/Harness.SharedKernel/Security/PersistenceSanitizer.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.SharedKernel/Security/PersistenceSanitizer.cs)
- [`src/Harness.SharedKernel/Security/SecretTextProtector.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.SharedKernel/Security/SecretTextProtector.cs)
- [`src/Harness.Persistence.Postgres/PostgresAuditEventStore.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Postgres/PostgresAuditEventStore.cs)
- [`src/Harness.Persistence.Sqlite/SqliteAuditEventStore.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Sqlite/SqliteAuditEventStore.cs)
- [`src/Harness.Persistence.Postgres/PostgresDurableExecutionEngine.Helpers.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Postgres/PostgresDurableExecutionEngine.Helpers.cs)
- [`tests/Harness.IntegrationTests/Security/CanarySecretPersistenceTests.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/tests/Harness.IntegrationTests/Security/CanarySecretPersistenceTests.cs)

Análise:
- A sanitização (`PersistenceSanitizer.SanitizeCriticalJson`) é aplicada estritamente **antes** do cálculo do hash encadeado do ledger (`AuditLedgerHash.Compute`) e **antes** da persistência no banco e na outbox em todos os funis de escrita.
- O modo é *fail-closed*: se qualquer segredo reconhecível sobreviver à sanitização em canal crítico (ledger/receipt), a escrita é recusada com `SecretPersistenceException` em vez de persistida.
- A estrutura de payloads JSON é preservada parsing-a recursivamente, redigindo campos sensíveis e valores sem quebrar o esquema JSON.
- A fronteira delimitada (preservação do corpo de texto de negócio inserido pelo usuário em `solicitations`, `demands`, `conversation_messages`, etc.) é defensável e documentada: protege a integridade do histórico do usuário sem permitir que tais segredos vazem sem redação para canais de evidência, auditoria, outbox, logs, exportações ou backups.

Findings críticos / altos / médios / baixos:
- Nenhum finding identificado.

STATUS: PASS
JUSTIFICATIVA: Politica única de sanitização aplicada no funil de escrita antes do hash do ledger e da gravação no banco; fail-closed em canais críticos; fronteira de negócio fundamentada; e teste regressivo de canário dinâmico validado em todas as tabelas, backups e dumps.

---

## 0B1 — attestation de sandbox
Arquivos revisados:
- [`src/Harness.Host/Execution/SandboxAttestationService.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Execution/SandboxAttestationService.cs)
- [`src/Modules/Harness.Modules.Execution/Infrastructure/Sandbox/DockerSandboxProvider.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Modules/Harness.Modules.Execution/Infrastructure/Sandbox/DockerSandboxProvider.cs)
- [`src/Harness.Persistence.Sqlite/SqliteSandboxAttestationStore.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Sqlite/SqliteSandboxAttestationStore.cs)
- [`src/Harness.Persistence.Postgres/PostgresSandboxAttestationStore.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Postgres/PostgresSandboxAttestationStore.cs)
- [`src/Harness.Host/Agents/AgentRunOrchestrator.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Agents/AgentRunOrchestrator.cs)
- [`tests/Harness.IntegrationTests/Security/SandboxAttestationTests.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/tests/Harness.IntegrationTests/Security/SandboxAttestationTests.cs)

Análise:
- `SandboxActive` não é mais um literal: é derivado da attestation empírica emitida pelo provider (`ISandboxProvider.AttestAsync`), que inspeciona (`docker container inspect`) se o container possui `ReadonlyRootfs = true`, `NetworkMode = "none"` e limites de CPU/Memória/PIDs aplicados.
- A mera criação do container não é aceita como prova; se a inspeção não confirmar todas as 4 fronteiras de contenção, `Verified` é retornado como `false`.
- A attestation é vinculada estritamente ao `AttemptId`. Atestações de outras tentativas não ativam o sandbox de outra execution.
- A primeira emissão prevalece no banco (`INSERT OR IGNORE` / `ON CONFLICT DO NOTHING`), impedindo bênção ou alteração retroativa de attestation durante a execução.
- Em modo sem isolamento ou ausência de provider, a atestação registra a ausência como fato (`Verified = false`, `Provider = "none"`), levando a política de ferramentas a negar chamadas críticas (`sandbox_required`).

Findings críticos / altos / médios / baixos:
- Nenhum finding identificado.

STATUS: PASS
JUSTIFICATIVA: Evidência de contenção empírica obtida diretamente do runtime antes da autorização de ferramentas de risco; attestation única por tentativa; garantia de que a primeira emissão prevalece e comportamento fail-closed em todas as bordas.

---

## 0B2 — broker de ferramentas
Arquivos revisados:
- [`src/Modules/Harness.Modules.Tools/Application/ToolCallBroker.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Modules/Harness.Modules.Tools/Application/ToolCallBroker.cs)
- [`src/Harness.Host/Agents/ToolCallJournalAdapter.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Agents/ToolCallJournalAdapter.cs)
- [`src/Harness.Persistence.Sqlite/SqliteToolCallJournalStore.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Sqlite/SqliteToolCallJournalStore.cs)
- [`src/Harness.Persistence.Postgres/PostgresToolCallJournalStore.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Postgres/PostgresToolCallJournalStore.cs)
- [`tests/Harness.IntegrationTests/Security/ToolCallBrokerTests.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/tests/Harness.IntegrationTests/Security/ToolCallBrokerTests.cs)

Análise:
- Todo efeito invocado via `ToolCallBroker.InvokeAsync` é validado individualmente.
- Perfis de papel são estritamente fechados: `Chief` é impedido de executar ferramentas (`chief_cannot_execute_tools`) e `Critic` é estritamente read-only (`critic_is_read_only`), negando qualquer chamada mutacional.
- Todos os caminhos em `request.Paths` são validados iterativamente contra o PEP (`_pep.AuthorizeAsync`); autorizar apenas pelo primeiro caminho é impossibilitado.
- Idempotência real: a chave derivada de idempotência é buscada no `tool_call_journal`. Caso a chamada já tenha sido executada, o resultado anterior é retornado com `Replayed = true` sem re-executar o efeito.
- A saída é sanitizada via `PersistenceSanitizer.SanitizeText` e truncada sem truncar caracteres UTF-8 multibyte.
- Limite declarado: o fato de chamadas internas de binários CLI de terceiros ocorrerem dentro da sandbox é explicitamente documentado, sendo essa a razão pela qual a sandbox atestada do 0B1 atua como fronteira complementar.

Findings críticos / altos / médios / baixos:
- Nenhum finding identificado.

STATUS: PASS
JUSTIFICATIVA: Gateway de ferramentas tipado por chamada com journal persistente de decisões e resultados; restrições de papel invioláveis; verificação exaustiva de todos os caminhos de arquivo no PEP; idempotência garantida; e redação de saídas.

---

## 0C1/0C2 — merge intent e reconciliação
Arquivos revisados:
- [`src/Harness.Host/WorkBoard/TaskIntegrationService.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/WorkBoard/TaskIntegrationService.cs)
- [`src/Harness.Host/WorkBoard/MergeReconciliationBackgroundService.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/WorkBoard/MergeReconciliationBackgroundService.cs)
- [`src/Harness.Persistence.Sqlite/SqliteMergeIntentStore.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Sqlite/SqliteMergeIntentStore.cs)
- [`src/Harness.Persistence.Postgres/PostgresMergeIntentStore.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Postgres/PostgresMergeIntentStore.cs)
- [`tests/Harness.IntegrationTests/Coordination/MergeIntentReconciliationTests.cs`](file:///Users/mateus/Documents/harness-poseidon-backend/tests/Harness.IntegrationTests/Coordination/MergeIntentReconciliationTests.cs)
- [`docs/architecture/audits/bruna/execucao/RELATORIO-FASE-0.md`](file:///Users/mateus/Documents/harness-poseidon-backend/docs/architecture/audits/bruna/execucao/RELATORIO-FASE-0.md)

Análise:
- Concorrência multi-host: "Um único merge ativo por repositório" é garantido por um índice único parcial direto no banco de dados (`0115_merge_intents.sql`), substituindo travas em memória.
- A intenção de merge é registrada antes do efeito Git. O commit SHA resultante é capturado e persistido.
- Se o Host perder a posse do fencing token por expiração de lease durante o merge, `TryRecordMergedAsync` recusa o registro e a reconciliação conclui o lado factual.
- Reconciliador (`MergeReconciliationBackgroundService`): varre intents não concluídos. Se o commit existir no repositório Git mas a transição no banco não tiver sido finalizada, a reconciliação conclui a transição (`settled`). Se o banco afirmar integração sem que o SHA exista no repositório Git, a intenção é reaberta (`reopened`).
- Merges abortados (conflitos Git) liberam o repositório sem alterar o estado do card.

Findings críticos / altos / médios / baixos:
- Nenhum finding identificado.

STATUS: PASS
JUSTIFICATIVA: Registro prévio de intenção com trava multi-host no banco; prevenção de concorrência por fencing token; captura empírica do SHA do commit Git; e serviço de reconciliação em background assegurando a convergência eventual entre o repositório Git e a base de dados.

---

## CONSOLIDADO
Testes executados:
Comandos:
```bash
tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj --filter "FullyQualifiedName~CanarySecret|FullyQualifiedName~SandboxAttestation|FullyQualifiedName~ToolCallBroker|FullyQualifiedName~MergeIntent"
```

Resultados:
- **Total de testes no filtro**: 18
- **Falhas**: 0
- **Aprovados**: 18
- **Tempo de execução**: 4s

CORREÇÕES EXIGIDAS DO CLAUDE:
*(Vazia — todos os blocos aprovados com STATUS: PASS)*
