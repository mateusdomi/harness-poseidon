# Evidência F2-DOGFOOD-2b — pipeline dogfood ponta a ponta

Data: 2026-07-19.

O cenário dogfood completo foi comprovado em um único fluxo automatizado (`DogfoodPipelineTests`), com o Host real em modo `fake` de execução isolada e um repositório externo fixture dentro da raiz controlada:

1. **Solicitação humana** entra pelo chat do projeto; o turno do Chief é enfileirado e o worker durável o completa com o executor estruturado.
2. **Demanda** proposta pelo Chief é materializada atomicamente na completion (solicitação interna backing + demanda `open` + `demand.created`).
3. **Tarefa** é criada a partir da demanda pela API canônica do quadro, com instrução v1 imutável.
4. **Tentativa de negócio** inicia pelo `IWorkChainStore` com o engenheiro como produtor.
5. **Execução isolada** roda por `POST /attempts/{id}/isolated-executions`: claim de escopo persistente, branch/worktree reais no repositório externo, sandbox, executor, conclusão com commit SHA, cleanup e liberação do claim — a branch de tarefa permanece no repositório.
6. **Evidência** referencia `workspace:<branch>@<commit>` e conclui a tentativa (`awaiting_review`).
7. **Critic independente** (`critic-qa` ≠ produtor) aprova com rationale e a tarefa chega a `done` — actor–critic respeitado.
8. **Auditoria ponta a ponta**: `GET /api/v1/audit-events` exibe a trilha completa (`chat.turnCompleted`, `demand.created`, `task.created`, `attempt.workspaceClaimed`, `attempt.workspaceCompleted`, `attempt.workspaceCleaned`).

Gate integral `tools/backend/verify.sh` com exit 0: frontend lint/typecheck/build e 270/270 testes; backend restore locked, format sem mudanças, build Release com `0 Aviso(s)`/zero erros e **183/183** testes (`Unit 96`, `Integration 45`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`); inventário Docker do Harness vazio. `frontend/**` e `docs/frontend/**` sem edição.

Pendências conscientes para o GNG-3: smoke com Codex real (`HARNESS_RUN_REAL_AGENT_TESTS=true`, modo docker, consome cota), execução dos E2E do frontend contra a API real e homologação visual/humana — o GNG-3 não é declarado sem aceite humano registrado.
