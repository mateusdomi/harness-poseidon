# Evidência F2-DOGFOOD-1e.2 — fiação DI e API tipada da execução isolada

Data: 2026-07-19.

A execução isolada agora é uma capacidade configurada do Host. `Harness:IsolatedExecution` liga o recurso por modo fechado (`Disabled` padrão, `Fake` determinístico sem Docker/cota, `Docker` com `DockerSandboxProvider` + `CodexCliSandboxExecutorFactory` reais), com raiz controlada, imagens, limites, heartbeat e lease tipados em `IsolatedExecutionSettings`. O provider Docker e o orquestrador são singletons resolvidos lazily — o Host inicia normalmente sem Docker instalado e o recurso falha fechado com Problem Details quando desabilitado.

`POST /api/v1/attempts/{attemptId}/isolated-executions` recebe contrato tipado (`instruction`, `scopeClaims`, `baseReference?`), exige sessão local, resolve tentativa→tarefa→projeto no tenant da sessão e valida a política do projeto externo: `repositoryUrl` local obrigatório, existente e contido na raiz controlada. O Host deriva branch determinística `task/attempt-<ulid>`, worktree por tentativa sob a raiz controlada, owner e idempotency key canônica, e devolve `IsolatedExecutionResponse` completo (status fechado, snapshot do workspace com estados codificados, conflitos tipados, metadados da execução e erro sanitizado). O OpenAPI canônico foi republicado em `docs/contracts/openapi.json` e o teste de drift passou.

O teste de integração sobe o Host em modo `fake` por configuração de linha de comando e comprova: 404 para tentativa inexistente; execução completa via API com branch real criada, worktree removida, claims liberados e replay idempotente sem nova execução; 409 para projeto sem repositório declarado; e 409 no Host padrão com o recurso desabilitado.

Gate: `dotnet format` sem mudanças; build Release com zero warnings/erros; backend 181/181 (`Unit 96`, `Integration 43`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`). `frontend/**` e `docs/frontend/**` sem edição. Restante do F2-DOGFOOD: smoke com Codex real (`HARNESS_RUN_REAL_AGENT_TESTS=true`) e o pipeline dogfood completo solicitação→Chief→tarefa→execução isolada→critic/gate→auditoria.
