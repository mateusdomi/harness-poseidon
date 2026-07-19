# Evidência F2-DOGFOOD-3a — smoke com agente de IA real no pipeline isolado

Data: 2026-07-19.

Com a cota do Codex indisponível, o usuário autorizou explicitamente o uso da CLI de agente local **antigravity** (`agy`) para o smoke real, sem custo de cota. `RealAgentSmokeTests` (classificado, executa somente com `HARNESS_RUN_REAL_AGENT_TESTS=true`; CLI configurável via `HARNESS_REAL_AGENT_CLI`) comprovou o pipeline de tentativa isolada com um **agente de IA real produzindo trabalho real**:

1. cadeia de negócio criada via API (perfil→organização→projeto→tarefa→tentativa);
2. `IsolatedAttemptOrchestrator` adquiriu o claim persistente e criou branch `task/real-agent-smoke` + worktree reais no repositório fixture;
3. o executor real invocou `agy --print --dangerously-skip-permissions --add-dir <worktree>` com a instrução da tarefa; o agente **criou de fato `STATUS.md` com o conteúdo pedido dentro da worktree** e devolveu structured output validado pelo `ChiefTurnOutputContract` (com um turno de reparo disponível);
4. a tentativa concluiu (`Completed`, executor `real-cli`) e a política de cleanup preservou a worktree suja com o claim retido (`cleanup pendente`) — exatamente o comportamento projetado para trabalho não commitado de agente.

Execução real registrada: 1/1 aprovado em 6s com o agy autenticado da máquina (a sondagem manual prévia confirmou que o agy exige `--add-dir` para escrever no diretório de trabalho — falha inicial diagnosticada e corrigida). O smoke roda o agente no host (fora do sandbox Docker do Harness): isso está registrado como **modo inseguro aceito pelo usuário para smoke**; o caminho de produção continua sendo `CodexCliAgentExecutor`, que falha fechado sem prova de sandbox. Quando a cota do Codex voltar, o mesmo cenário se aplica ao protocolo app-server real já comprovado em `evidence/F2-DOCKER-ATTEMPT-COMPOSITION.md`.

Gate: format sem mudanças; build Release zero warnings/erros; suíte integral 210/210 com o smoke gated (`Unit 116`, `Integration 52`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`).
