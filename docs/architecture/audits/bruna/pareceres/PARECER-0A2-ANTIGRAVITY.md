AUDITORIA FASE 0A2

Branch: fix/poseidon-phase0a2-lease-and-context
Commit: 8752332b
Arquivos revisados:
- `docs/INDEX.md`
- `docs/backend/runbooks/recovery.md`
- `governance/manifest.yaml`
- `src/Harness.Host/Observability/PoseidonTelemetry.cs`
- `src/Harness.Host/Workers/ChiefContextComposer.cs`
- `src/Harness.Host/Workers/ChiefContextStrategyOptions.cs`
- `src/Harness.Host/Workers/ChiefTurnBackgroundService.cs`
- `src/Harness.Host/Workers/ChiefTurnWorkerOptions.cs`
- `src/Harness.Persistence.Abstractions/Agents/IChiefTurnStore.cs`
- `src/Harness.Persistence.Abstractions/Conversations/IConversationStore.cs`
- `src/Harness.Persistence.Postgres/PostgresConversationStore.ChiefTurns.cs`
- `src/Harness.Persistence.Postgres/PostgresConversationStore.cs`
- `src/Harness.Persistence.Sqlite/SqliteConversationStore.ChiefTurns.cs`
- `src/Harness.Persistence.Sqlite/SqliteConversationStore.cs`
- `tests/Harness.IntegrationTests/Coordination/ChiefLongProjectContextTests.cs`
- `tests/Harness.IntegrationTests/Coordination/ChiefTurnLeaseRenewalTests.cs`
- `tests/Harness.IntegrationTests/Persistence/ConversationChiefStoreBehavior.cs`

Renovação de lease: O método `MaintainActivityHeartbeatAsync` no [ChiefTurnBackgroundService.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Workers/ChiefTurnBackgroundService.cs#L142-L170) renova ativamente o lease via `turns.TryRenewAsync` no loop do `PeriodicTimer` a cada 30 segundos. Ele não é mais apenas uma publicação visual de atividade.

Fencing: A renovação preserva o fencing estritamente. Tanto no [SqliteConversationStore.ChiefTurns.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Sqlite/SqliteConversationStore.ChiefTurns.cs#L63-L71) quanto no [PostgresConversationStore.ChiefTurns.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Postgres/PostgresConversationStore.ChiefTurns.cs#L272-L281), a instrução `UPDATE` filtra obrigatoriamente por `lease_owner_id=$owner AND lease_fencing_token=$fencing` com verificação de `active_fencing_token=$fencing` em estado `processing`. Um dono antigo sem fencing afeta 0 linhas e recebe `Renewed = false`.

Aborto da inferência: Se `TryRenewAsync` indicar perda de fencing (`!renewal.Renewed`), `MaintainActivityHeartbeatAsync` executa `await leaseLost.CancelAsync()`. O token `leaseLost.Token` cancela imediatamente a chamada `_agent.ExecuteAsync(...)` (parando os gastos de cota com a LLM). A exceção `OperationCanceledException` é capturada especificamente para `leaseLost` e encerra o ciclo sem chamar `CompleteAsync` nem `FailAsync` (sem escrita tardia nem marcação indevida de falha).

Recuperação de turno abandonado: Se um worker cair, a renovação para. Após o vencimento (`now > lease_expires_at`), o método `AcquireNextAsync` seleciona o turno `processing` expirado e o reatribui para outro worker incrementando o `fencing_token`. Não há risco de turno preso.

Alinhamento lease/heartbeat: O método `Validate()` em [ChiefTurnWorkerOptions.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Workers/ChiefTurnWorkerOptions.cs#L19-L37) valida na inicialização que `LeaseDuration` deve ser pelo menos 3 vezes o `ActivityHeartbeatInterval` (`LeaseDuration >= ActivityHeartbeatInterval * 3`), assegurando margem para eventuais oscilações I/O.

Métricas: Adicionado o contador `poseidon.chief.turn.lease.count` em [PoseidonTelemetry.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Observability/PoseidonTelemetry.cs#L63-L66) registrando as fatias de resultado (`renewed`, `lost`, `expired`), além da tag `lease_lost` no contador global de turnos.

Seleção de histórico: O [ChiefContextComposer.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/Workers/ChiefContextComposer.cs#L132-L133) utiliza `ListRecentMessagesAsync`, que executa `ORDER BY id DESC LIMIT $limit` diretamente no banco e inverte a lista antes de devolver, garantindo que as mensagens mais recentes e decisões finais fiquem presentes no contexto.

Mensagem fundadora: Preservada de forma explícita via `GetFirstMessageAsync` (`ORDER BY id LIMIT 1`) e fixada na posição 0 do contexto (`Pinned = true, Critical = true`). Um `HashSet<string> seen` impede qualquer duplicação quando a mensagem fundadora também estiver dentro da janela das N mais recentes.

Orçamento de tokens: A estratégia `_strategy.Apply(items, _options.Budget)` trunca/compacta o meio do histórico mantendo o orçamento máximo de tokens estritamente respeitado.

Recuperação de notas: `_notes.ListAsync` recupera as notas duráveis gravadas em turnos anteriores e as reinjeta no contexto formatadas com proveniência explícita (`[nota durável · origem {SourceItemId} · registrada em {CreatedAt} UTC]`).

Isolamento de projeto/tenant: Todas as consultas de histórico e notas filtram obrigatoriamente por `tenant_id` e `project_id`. Testado contra vazamento em `NotesFromAnotherProjectNeverEnterTheContext`.

SQLite: Implementação nativa dos métodos `TryRenewAsync`, `ListRecentMessagesAsync` e `GetFirstMessageAsync` em `SqliteConversationStore`.

PostgreSQL: Implementação equivalente dos mesmos métodos em `PostgresConversationStore`, assegurando paridade semântica integral.

Escopo do diff: Restrito estritamente aos componentes da Fase 0A2 (Lease do Chefe, heartbeat com fencing, contextualização recente, notas duráveis, documentação associada e testes de regressão). Nenhuma alteração indevida em materialização de planos, sandbox, RAG ou frontend.

Findings críticos:
Nenhum.

Findings altos:
Nenhum.

Findings médios:
Nenhum.

Findings baixos:
Nenhum.

Testes executados:
Comandos:
- `tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj --filter "FullyQualifiedName~ChiefTurnLeaseRenewalTests|FullyQualifiedName~ChiefLongProjectContextTests"`
- `tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj --filter "FullyQualifiedName~SqliteIdentityCoreStoreTests"`
- `tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj --filter "FullyQualifiedName~PostgresSkipLockedPocTests"`
- `tools/backend/verify-governance.sh`

Resultados:
- `ChiefTurnLeaseRenewalTests` & `ChiefLongProjectContextTests`: 5/5 aprovados (0 falhas)
- `SqliteIdentityCoreStoreTests`: 1/1 aprovado (0 falhas)
- `PostgresSkipLockedPocTests`: 1/1 aprovado (0 falhas)
- `verify-governance.sh`: 0 erros, 0 avisos

STATUS:
PASS

JUSTIFICATIVA:
A auditoria técnica da Fase 0A2 comprovou a resolução completa dos riscos BR-005 e BR-006. A renovação de lease no batimento condicional ao fencing elimina a duplicação de chamadas LLM e custos dobrados; a perda de fencing aborta a inferência sem contaminar o estado durável do banco; turnos efetivamente abandonados continuam sendo recuperados por expiração natural de lease; a montagem do contexto lê a cauda mais recente mantendo o mandato fundador e reinjetando notas duráveis com proveniência e isolamento multi-tenant/projeto. A paridade entre os providers de banco e os testes de integração foram validados e aprovados sem ressalvas.

CORREÇÕES EXIGIDAS DO CLAUDE:
Nenhuma.
