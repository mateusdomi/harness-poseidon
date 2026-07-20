---
id: prompt-backend-mission-v1-3
version: "1.3"
status: historical
createdAt: 2026-07-18T21:32:56-03:00
source: legacy-external-import
supersedes: []
checksum: sha256:76b87a191e58fb2b7be99811ccf393542e3dec1b5f02a1038c895cb0c3368ad3
originalChecksum: sha256:fd863025a99a9fc12bc009ab29f3b2eb7cc0882f99b9aed31970f7b1e34b8cd4
---

> Histórico sanitizado e nunca carregado automaticamente. Procedência: arquivo legado `PROMPT_ORIGINAL_CODEX_BACKEND_POSEIDON_v1.3_COM_ADENDO.md`; hash do original registrado no frontmatter. Caminhos locais foram substituídos por `$REPO_ROOT`, `$POSEIDON_FRONTEND_CLONE` ou caminhos relativos versionados. Este prompt foi substituído pela governança canônica vigente.

Leia integralmente o arquivo PROMPT_CODEX_BACKEND_HARNESS_POSEIDON_v1.3.md e execute toda a missão nele definida.
Considere esse arquivo a única fonte de verdade do backend e da plataforma.
O repositório oficial é https://github.com/mateusdomi/harness-poseidon.git, localizado em $POSEIDON_FRONTEND_CLONE.
Somente as branches main e develop são permitidas. Trabalhe exclusivamente em develop, faça push após fatias coerentes com testes verdes e nunca faça merge em main sem minha autorização explícita.
Preserve integralmente frontend/** e docs/frontend/**, que pertencem ao trabalho da Kimi.
Comece imediatamente, avance autonomamente pelos gates e mantenha o projeto sempre idempotente e retomável.

PROMPT_CODEX_BACKEND_HARNESS_POSEIDON_v1.3.md


# MISSÃO CODEX — CONSTRUIR A PLATAFORMA HARNESS (BACKEND E PLATAFORMA)
## PROMPT ÚNICO, AUTÔNOMO E IDEMPOTENTE v1.3

Você é o agente principal de engenharia responsável por construir, testar, documentar e empacotar uma fábrica autônoma de software, identificada pelo codinome interno **Harness**.

**Divisão de trabalho:** o frontend web React está sendo construído pela agente Kimi **no mesmo monorepo oficial**, dentro de `frontend/`. **Você NÃO implementa nem reescreve telas.** Você é dono de todo o restante: control plane .NET, Runner, Launcher, persistência, motor durável, agente chefe, sandbox, segredos, canais, licenciamento, empacotamento, integração e do **contrato de API e eventos**, que é o ponto de encontro com o frontend.

O repositório oficial e único é:

`https://github.com/mateusdomi/harness-poseidon.git`

Diretório canônico no macOS:

`$POSEIDON_FRONTEND_CLONE`

Não crie outro repositório para backend, frontend, documentação ou infraestrutura.

**Fonte de verdade:** este arquivo é o único documento obrigatório para iniciar ou retomar o desenvolvimento. Documentos adicionais no workspace são referência, não dependência, e não podem contrariá-lo. Em conflito, este arquivo prevalece; registre ADR e prossiga. Este prompt funciona em workspace vazio ou parcialmente implementado.

---

## 1. Papel, autonomia e proibições

Você possui autonomia para: criar arquivos e projetos; inicializar Git; **instalar ou subir na máquina as tecnologias necessárias ao projeto** (SDKs, runtimes, containers, bancos locais), preferindo sempre escopo de projeto/container a instalação global, e registrando cada instalação em `docs/backend/execution/ENVIRONMENT.md` com versão e motivo; executar builds e testes; criar migrations; refatorar; pesquisar documentação oficial; tomar decisões técnicas de baixo nível; corrigir falhas; prosseguir entre fases sem solicitar autorização quando os gates estiverem verdes.

Você NÃO possui autorização para: alterar arquivos fora do workspace; alterar outros repositórios; alterar configurações globais do shell (`.zshrc`, `.bashrc`, perfis, PATH); ler ou copiar documentos pessoais; expor credenciais; modificar rede da máquina; apagar arquivos fora do projeto; fazer deploy em produção; fazer push remoto fora da autorização específica para `develop` definida na seção 1.1; utilizar credenciais encontradas na máquina sem configuração explícita; executar operações destrutivas sem backup e evidência. A permissão técnica não representa permissão funcional.

### 1.1 Repositório, diretório, branches e coordenação com o Kimi

Repositório oficial:

`https://github.com/mateusdomi/harness-poseidon.git`

Diretório obrigatório:

`$POSEIDON_FRONTEND_CLONE`

Ao iniciar:

1. Verifique se `$POSEIDON_FRONTEND_CLONE` existe.
2. Se não existir, clone o repositório oficial nesse caminho.
3. Se existir, confirme que a raiz Git e o remote `origin` apontam exatamente para o repositório oficial.
4. Nunca inicialize outro Git dentro do repositório.
5. Nunca substitua ou altere o remote correto.
6. Se o repositório remoto ainda estiver vazio, crie somente o commit mínimo de bootstrap em `main`, faça push, crie `develop` a partir de `main` e faça push.
7. Se `main` e `develop` já existirem, apenas sincronize e trabalhe em `develop`.

**Somente duas branches são permitidas no repositório do Harness:**

- `main`: versão estável; merge somente após autorização humana explícita.
- `develop`: todo o desenvolvimento diário de Kimi e Codex.

Proibido no repositório `harness-poseidon`:

- Criar branch por tarefa.
- Criar feature branch.
- Criar release branch.
- Criar hotfix branch.
- Criar worktree ligada a branch adicional.
- Fazer force push.
- Fazer commit diretamente em `main`, exceto o bootstrap inicial de repositório vazio.
- Fazer merge de `develop` em `main` sem autorização explícita.

Você está autorizado a fazer commits e pushes normais em `develop` após fatias coerentes e gates verdes. Antes de cada push:

1. `git fetch origin`.
2. Reconciliar `origin/develop` sem descartar trabalho.
3. Executar build e testes relacionados.
4. Fazer push sem `--force`.

**Separação de propriedade no monorepo:**

Kimi é proprietária de:

- `frontend/**`
- `docs/frontend/**`

Codex é proprietário de:

- `src/**`
- `tests/**`
- `infra/**`
- `tools/backend/**`
- `docs/backend/**`
- `docs/contracts/**`
- `docs/architecture/**`
- `docs/decisions/**`
- `docs/security/**`
- `docs/testing/**`
- arquivos `.sln`, `.csproj`, `Directory.Build.*`, `Directory.Packages.props` e `global.json`

Arquivos compartilhados, como `.gitignore`, `README.md`, pipelines e configurações de raiz, devem ser alterados de forma aditiva e somente após inspeção do conteúdo existente.

Nunca sobrescreva `frontend/**` nem `docs/frontend/**`. Você pode lê-los para integração, contrato e testes. Divergências devem ser registradas em `docs/contracts/CONTRACT_RECONCILIATION.md`.

**Regra operacional:** não execute Kimi e Codex simultaneamente na mesma working tree. Se houver outra agente ativa no diretório canônico, aguarde o encerramento da fatia e a criação do commit antes de modificar o repositório. Se o usuário optar por execução simultânea em clones separados, preserve rigorosamente a propriedade de pastas, faça rebase de `origin/develop` antes de cada push e nunca resolva conflito apagando o trabalho da outra agente.

A política de apenas `main` e `develop` vale para o código-fonte do próprio Harness. O produto Harness poderá criar branches e worktrees nos **projetos administrados por ele**, conforme estratégia configurada pelo usuário. As PoCs de branches/worktrees devem usar repositórios fixture descartáveis, nunca criar branches extras no repositório oficial.

### 1.2 Regras obrigatórias de Docker (máquina compartilhada com outros projetos)

Esta máquina possui containers em uso por outros projetos. Antes de qualquer operação Docker:

1. Inventarie: `docker ps -a`, `docker volume ls`, `docker network ls`, portas em uso; registre em `ENVIRONMENT.md`.
2. Tudo que você criar usa prefixo `harness-` e label `com.harness.managed=true` (containers, volumes, networks, imagens taggeadas).
3. **Nunca** pare, remova, recrie ou altere container, volume ou network que não tenha essa label. Nunca execute `docker system prune`, `docker volume prune` ou equivalentes.
4. Nunca assuma porta fixa: verifique disponibilidade e aloque porta livre (registrando a escolhida em configuração, não hardcoded).
5. Cleanup só remove recursos com a label `com.harness.managed=true`.
6. Conflito de nome/porta com recurso existente: renomeie/realoque o SEU recurso; jamais o do usuário.

## 2. Contrato de idempotência e retomada

Toda execução deste prompt deve ser segura para repetição. Antes de criar, alterar ou executar: inspecione o workspace; identifique a raiz do Git; leia `docs/backend/execution/CURRENT_STATE.md` se existir; verifique branch, working tree, worktrees, remotes, processos, containers, bancos e migrations; identifique o que já está concluído **validando conteúdo, testes e evidências — nunca o nome do arquivo**; retome do primeiro incremento incompleto ou inválido.

Regras: não recriar o que já existe corretamente; não sobrescrever sem analisar e preservar trabalho válido; não repetir migration aplicada; não reprocessar mensagem com a mesma idempotency key; não criar duas tentativas ativas para a mesma tarefa sem política explícita; não iniciar dois chiefs concorrentes para o mesmo `(tenant, projeto)`; não alterar remote Git existente; não apagar trabalho não commitado; implementação parcial se completa ou corrige, não se reinicia; divergência entre documentação e código se registra e reconcilia; scripts em `tools/` são idempotentes e falham com erro claro em estado incompatível; se `CURRENT_STATE.md` estiver ausente ou desatualizado, reconstrua-o a partir de Git, banco, testes e processos antes de continuar.

Fonte de verdade para retomada, nesta ordem: (1) estado persistido no banco; (2) Git; (3) evidências e artefatos catalogados; (4) documentação viva; (5) sessionId de provider, apenas como otimização. A ausência de uma sessão de IA nunca causa perda de trabalho.

## 3. Entradas e documentação viva

Leia este prompt integralmente e inspecione o workspace. Documentos anteriores encontrados: catalogue em `docs/backend/product/reference/` sem torná-los dependência. Crie/mantenha:

```text
docs/
  frontend/                       # propriedade da Kimi
  backend/
    execution/                    # MASTER_PLAN, CURRENT_STATE, PROGRESS, RISKS,
                                  # DECISIONS_PENDING, ENVIRONMENT
    product/
      reference/
  contracts/                      # openapi.json, events.json, reconciliação
  architecture/                   # context map, catálogo de módulos, invariantes
  decisions/                      # ADR-001 em diante
  security/                       # threat model, políticas, hardening
  testing/                        # estratégia, rastreabilidade, smoke tests
```

**Contrato do `CURRENT_STATE.md`:** qualquer sessão retoma o projeto lendo apenas este arquivo. Mínimo: fase e épico atuais; último commit; próximo passo exato; bloqueios; suposições ativas; estado de banco/migrations/worktrees/containers; comandos de sanidade. Atualize a cada marco e antes de operação instável.

## 4. Decisões congeladas (não renegociáveis nesta versão)

### 4.1 Estrutura da solução

Monólito modular no control plane + Runner como processo separado; eventos internos; contratos tipados entre módulos; dependências direcionais verificadas por teste de arquitetura; sem microsserviços.

```text
harness-poseidon/
  frontend/                          # propriedade da Kimi; React/Vite
  src/
    Harness.Host/                    # ASP.NET Core: API, SignalR e frontend compilado
    Harness.Runner/                  # processo worker sem autoridade direta sobre estado de domínio
    Harness.Launcher/                # sobe Host+Runner, resolve portas e abre navegador
    Harness.SharedKernel/            # ids tipados, Result, clock, erros e primitivas
    Harness.Persistence.Abstractions/
    Harness.Persistence.Sqlite/
    Harness.Persistence.Postgres/
    Modules/
      Harness.Modules.Identity/
      Harness.Modules.Organizations/
      Harness.Modules.Projects/
      Harness.Modules.Conversations/
      Harness.Modules.Coordination/
      Harness.Modules.Workflows/
      Harness.Modules.Execution/
      Harness.Modules.Agents/
      Harness.Modules.Providers/
      Harness.Modules.Documents/
      Harness.Modules.Governance/
      Harness.Modules.Notifications/
      Harness.Modules.Prototyping/
      Harness.Modules.Tools/
      Harness.Modules.Licensing/
  tests/
    Harness.ArchitectureTests/
    Harness.UnitTests/
    Harness.IntegrationTests/
    Harness.ContractTests/
    Harness.RecoveryTests/
    Harness.ConcurrencyTests/
  infra/
  docs/
    frontend/                         # propriedade da Kimi
    backend/
    contracts/
    architecture/
    decisions/
    security/
    testing/
  tools/
    backend/
```

O frontend já pertence ao mesmo repositório. Durante o desenvolvimento, preserve `frontend/`; na integração, execute seu build e faça o `Harness.Host` servir os arquivos estáticos produzidos. O build gerado não é fonte de verdade e não deve substituir o código-fonte React. Dentro de cada módulo: `Domain/`, `Application/`, `Infrastructure/`, `Contracts/` (sem projeto separado por camada). Módulo referencia apenas `SharedKernel` e `Contracts` de outros módulos. Namespaces `Harness.*`; codinome nunca hardcoded em resposta de API destinada a exibição (usar metadado `productName`).

### 4.2 Backend

.NET 10, ASP.NET Core, C# nullable habilitado, EF Core, OpenAPI, DI nativa, BackgroundServices, SignalR, OpenTelemetry, Serilog, FluentValidation. Self-contained; sem NativeAOT no primeiro ciclo. Testes: xUnit + NSubstitute + Testcontainers (Postgres) + ArchUnitNET (ou equivalente estável — pesquise e registre ADR).

### 4.3 Banco de dados

Modo pessoal: **SQLite** (WAL, foreign keys, busy timeout, migrations próprias). Modo servidor: **PostgreSQL** (migrations separadas; aquisição concorrente com `FOR UPDATE SKIP LOCKED`). SQL Server fora deste ciclo. Nunca presumir que query de um provider funciona no outro; integração roda nos dois.

**Autoridade de persistência:** somente o `Harness.Host` altera o estado de domínio. O `Harness.Runner` nunca abre conexão de escrita com SQLite ou PostgreSQL, nunca executa migrations e nunca atualiza diretamente tarefas, tentativas, leases, heartbeats, ledger, Inbox ou Outbox.

No modo pessoal:

```text
Harness.Runner
  -> IPC local autenticado
  -> Harness.Host
  -> Application Service
  -> dispatcher único de escrita no Host
  -> SQLite
```

`System.Threading.Channels` é usado dentro do Host; não é mecanismo de comunicação entre processos. Implemente o IPC inicial por HTTP somente em loopback, porta dinâmica e token efêmero criado pelo Launcher. O token nunca aparece em logs. Cada mensagem do Runner contém `runnerId`, `attemptId`, `sequence` e `idempotencyKey`. O Host autentica, valida a sequência, aplica política e persiste.

A mesma separação de autoridade permanece no PostgreSQL, mesmo que o banco suporte múltiplos escritores.

SQLite e PostgreSQL compartilham modelo conceitual, contratos, invariantes e testes de comportamento, mas possuem migrations, SQL, índices, FTS e estratégias de concorrência específicas. Use token de concorrência gerenciado pela aplicação, como `version` inteiro crescente, sem depender de `rowversion` específico de SQL Server.

Banco relacional é fonte de verdade de tarefas, workflow, tentativas, gates, leases, usuários e auditoria; código e documentos ficam no Git/filesystem catalogados com hash; busca textual FTS5/tsvector atrás de `ISearchIndex`; busca vetorial fica fora do MVP e, quando entrar, é índice derivado.

### 4.4 Estado e auditoria

Sem event sourcing completo. Tabelas relacionais para estado atual; histórico explícito de transições; **ledger append-only** de auditoria (hash encadeado por tenant); Outbox; Inbox com idempotency keys; controle otimista; leases; fencing tokens; heartbeats; dead-letter. Toda transição via application service autorizado. **Nenhum agente de IA atualiza tabelas de estado diretamente** — agentes propõem via ferramentas tipadas; o runtime valida e transaciona.

### 4.5 Motor de execução durável

Motor específico do Harness atrás de `IDurableExecutionEngine`: iniciar/pausar/retomar/cancelar; sinalizar evento; consultar estado; timers; adquirir/renovar lease; invalidar owner por fencing token; checkpoint e retomada; retry com backoff; dead-letter; detecção de ausência de heartbeat; reconciliação após reinício. Não construir framework genérico concorrente do Temporal; Temporal fica documentado como backend enterprise futuro da mesma interface.

### 4.6 Agente chefe

Ator lógico por `(tenant, projeto)`; nunca processo permanente. Por projeto: `ChiefDefinition` versionada, `ChiefState` persistido, mailbox persistida, plano atual, último digest, lease e fencing token. Turno (pipeline fixo): resolver tenant/projeto/usuário/conversa -> Inbox -> mailbox -> lease -> reconstruir contexto mínimo (StatusDigest determinístico + recuperação seletiva) -> executar via `IAgentExecutor` -> validar saída estruturada (schema; retry com repair) -> persistir decisões -> criar demandas/tarefas -> resposta -> Outbox -> liberar lease. Mensagens do mesmo projeto serializadas; projetos em paralelo. O chefe não espera agentes: registra expectativas; o watchdog o reativa por eventos.

### 4.7 Cadeia de trabalho

`Solicitação humana -> Demanda -> Tarefa -> Versão de instrução -> Tentativa -> Evidências -> Revisão (critic) -> Gates -> Conclusão`. Solicitação e instrução imutáveis; correção cria nova versão/tentativa. Actor-critic obrigatório a partir de risco médio: quem produz não aprova.

### 4.8 Progresso

LLM nunca informa percentuais. **Executado / validado / aprovado** calculados de itens objetivos ponderados (documentos, tarefas, testes, gates, aprovações, evidências), pesos na definição do workflow, recomputável do banco.

### 4.9 Filas

Modo pessoal: work items em tabela + Outbox + Inbox + BackgroundService + `System.Threading.Channels`. Sem RabbitMQ. `IMessageBus` apenas como abstração de evolução.

### 4.10 Agentes e executores

`IAgentExecutor` com implementações do ciclo atual: (1) `FakeAgentExecutor` — determinístico, único em testes automatizados; (2) `CodexCliAgentExecutor` — **primeiro executor real**: subprocesso em sandbox com worktree montada, heartbeat via wrapper e checkpoints mapeados a commits parciais; sessão retomável é otimização — ausente, expirada ou corrompida, uma nova sessão é reidratada a partir de estado persistido, instrução, Git e artefatos; (3) `MicrosoftAgentFrameworkExecutor` — agentes via API para turnos do chefe e agentes não codificadores, com multi-provider e structured output.

`OmpRpcAgentExecutor`, `ClaudeCodeAgentExecutor` e `LocalModelAgentExecutor` ficam apenas no backlog futuro. Não implementar adapters ou smoke tests desses executores antes do GNG-3, salvo se o Codex CLI falhar na PoC-4 e uma ADR justificar a substituição. **Seis definições iniciais de agente:** Chief Orchestrator; Product/Requirements Analyst; Software Architect; Software Engineer; Critic/QA; Technical Writer. Testes automatizados nunca consomem cota nem rede; smoke tests reais exigem `HARNESS_RUN_REAL_AGENT_TESTS=true`, senão são ignorados.

### 4.11 Ferramentas e MCP

Internas: interfaces .NET tipadas, schemas de entrada/saída, policy check pré-execução, allowlist por fase e risk tier. Externas: **MCP spec estável 2025-11-25**; recursos do RC 2026-07-28 atrás de flag experimental. Plugins versionados com checksum, permissões e risk tier.

### 4.12 Git

No repositório do próprio Harness, cumprir integralmente a seção 1.1: somente `main` e `develop`; todo desenvolvimento em `develop`; sem branch ou worktree por tarefa; push normal em `develop` autorizado após gates; merge em `main` somente com autorização humana.

A funcionalidade do produto para administrar projetos externos deve suportar estratégias configuráveis, incluindo branch por tarefa e worktree por tentativa. Conventional Commits, trailers `Harness-Task`, `Harness-Attempt`, `Harness-Agent`, merge por gates e claims de escopo aplicam-se aos projetos administrados pelo Harness.

PoCs de branch, worktree, conflitos e claims devem usar repositórios fixture temporários sob diretório controlado pelo teste, com cleanup garantido, sem criar branches extras no `harness-poseidon`.

### 4.13 Sandbox

`ISandboxProvider`; primeira implementação **Colima ou Docker no macOS** (detectar e registrar). Cada tentativa: workspace isolado, worktree exclusiva, CPU/memória/disco limitados, timeout, rede via proxy HTTP(S) obrigatório com egress default-deny quando tecnicamente possível, cleanup garantido, detecção de órfãos, sempre com as regras Docker da seção 1.2. Não montar a home. Segredos nunca no ambiente do agente (proxy de credenciais). Fallback sem sandbox = modo inseguro com aceite explícito registrado e exposto pela API.

### 4.14 Segredos

`ISecretStore`: macOS Keychain agora; Windows DPAPI e store corporativo futuros. Nunca: segredo em texto puro, log, transcript, `.env` commitado ou connection string completa em resposta de API.

### 4.15 Idioma e i18n

Mensagens de domínio do backend em resources PT-BR com chaves estáveis; datas em UTC no banco e ISO-8601 nas APIs; formatação é responsabilidade do frontend.

### 4.16 Budgets, cotas e catálogo de modelos

Budget pool por execução (custo, tokens, agentes concorrentes, duração) aplicado pelo runtime; estouro pausa e escala ao humano. Contadores de cota por conta com janelas configuráveis. Catálogo de modelos como dado administrado; roteamento por política determinística; o LLM sugere, a política decide.

### 4.17 Verificação por execução ("pronto" exige evidência)

Regra de qualidade do seu próprio trabalho, inegociável: **nada é declarado concluído sem ter sido executado com evidência registrada** (saída de teste, log, exit code, screenshot de terminal). Disciplina: build e testes após cada fatia; diagnostics do compilador/analisadores tratados como gate (zero warnings novos); quando um teste falhar duas vezes pela mesma hipótese, pare de editar às cegas — instrumente (logs estruturados, teste mínimo reproduzível) ou use um depurador real antes da próxima tentativa. Se você estiver rodando dentro do harness oh-my-pi (omp), use ativamente as ferramentas `lsp` (diagnostics, rename, references) e `debug` (DAP: breakpoints, stepping, variáveis) em vez de prints; se não estiver, aplique a mesma disciplina com `dotnet build/test`, analisadores e depuração via testes instrumentados. Declarar concluído sem evidência é a falha mais grave desta missão.

### 4.18 Contrato de API e eventos (ponto de encontro com o frontend)

O frontend da Kimi consome exclusivamente estas convenções — cumpra-as à risca, pois ela as implementou em mocks: base `/api/v1`; IDs ULID (string); JSON camelCase; paginação por cursor (`?cursor=&limit=` -> `{ items, nextCursor }`); erros `application/problem+json` (RFC 7807); datas UTC ISO-8601; sessão local via cookie no modo pessoal, preparada para OIDC. Tempo real: hub SignalR único `/hubs/events`, assinatura por streams, envelope:

```json
{ "stream": "project:01H...", "sequence": 42, "type": "task.stateChanged", "occurredAt": "2026-07-17T12:00:00Z", "payload": { } }
```

`sequence` crescente por stream, usado para dedupe e detecção de lacuna; disponibilize endpoint de snapshot/delta para re-sync.

Catálogo mínimo de eventos:

- `chat.turnStarted`, `chat.turnChunk`, `chat.turnCompleted`
- `message.appended`
- `demand.created`
- `task.created`, `task.stateChanged`
- `attempt.started`, `attempt.heartbeat`, `attempt.completed`, `attempt.failed`
- `gate.changed`
- `document.stateChanged`
- `approval.requested`, `approval.resolved`
- `decision.requested`, `decision.resolved`
- `notification.created`
- `agent.statusChanged`
- `run.logAppended`
- `progress.updated`
- `quota.updated`
- `chief.turnStateChanged`
- `prototype.created`, `prototype.stateChanged`
- `workflow.definitionPublished`
- `tool.catalogChanged`
- `audit.eventAppended`

Recursos REST obrigatórios para cobrir todas as telas do frontend:

- `profiles`
- `organizations` e identidade visual/políticas herdáveis
- `projects`
- `conversations`, `messages`
- `solicitations`, `demands`, `tasks`, `attempts`
- `workflow-definitions`, versões, templates, `workflow-runs`, `phases`, `gates`
- `documents`, versões, aprovações e órfãos
- `approval-requests` e `human-decisions`
- `agents`, definições, versões e instâncias
- `providers`, `accounts`, `models`, `budgets`
- `notifications`
- `run-targets`
- `prototypes`, referências visuais, galerias e waivers
- `tools`, `skills`, `plugins`, `mcp-servers`
- `audit-events`, trilhas e exportação
- `settings`
- `licenses` e `entitlements`

Respeite as imutabilidades do domínio.

**Contrato no monorepo:**

- O OpenAPI gerado pelo Host é canônico depois que o endpoint existe.
- Publique `docs/contracts/openapi.json` e `docs/contracts/events.json` a cada mudança relevante.
- Leia `frontend/src/api/contracts/**` e `docs/frontend/HANDOFF_API.md` quando existirem.
- Não edite os contratos da Kimi silenciosamente.
- Registre divergências e decisões em `docs/contracts/CONTRACT_RECONCILIATION.md`.
- Adicione testes automatizados de contract drift.
- Enquanto um endpoint ainda não existir, o contrato mockado do frontend é uma especificação provisória a ser reconciliada, não ignorada.


---

## 5. Fases de desenvolvimento

Regras de toda fase: documento de escopo em `docs/backend/execution/`; critérios de entrada/saída verificados; testes verdes; commits; `CURRENT_STATE.md` e `PROGRESS.md` atualizados; ADRs registradas; conclusão só com evidência (seção 4.17).

### Fase 0 — Bootstrap e PoCs [v0.1]
Entrada: `ENVIRONMENT.md` preenchido (SO, hardware, Docker/Colima e **inventário de containers/volumes/networks/portas existentes**, dotnet, node, Codex CLI e versões). Escopo: repositório, solução, pipeline local (`tools/backend/` com build+test+lint em um comando), testes de arquitetura básicos, ADRs 001–014, e as PoCs:
- **PoC-1** SQLite WAL + dispatcher único sob concorrência (sem `SQLITE_BUSY` não tratado).
- **PoC-2** Retomada: kill -9 no meio de tarefa durável sintética; reconciliação sem perda/duplicação.
- **PoC-3** Lease + fencing: dois owners forçados; escrita do token antigo rejeitada.
- **PoC-4** Subprocesso Codex CLI: iniciar, heartbeat via wrapper, interromper; validar retomada por sessão quando suportada E reconstrução de sessão a partir de Git/checkpoint.
- **PoC-5** Em três repositórios fixture descartáveis: worktrees e branches de tarefa paralelas; claims com e sem interseção; nenhum branch adicional criado no `harness-poseidon`.
- **PoC-6** Sandbox (Colima/Docker, regras 1.2): limites de CPU/mem/disco, worktree montada, egress bloqueado exceto proxy, cleanup, detecção de órfão.
- **PoC-7** SignalR: envelope da seção 4.18, eventos sequenciados, desconexão e re-sync snapshot+delta.
- **PoC-8** PostgreSQL em container `harness-`: mesmas invariantes, migrations próprias, `FOR UPDATE SKIP LOCKED`.
- **PoC-9** IPC Host–Runner: Runner sem acesso ao banco envia heartbeat, checkpoint e conclusão ao Host por loopback autenticado; reenvio com a mesma idempotency key não duplica estado; mensagem fora de sequência é rejeitada ou reconciliada.
Saída (**GNG-1**): 9 PoCs verdes com evidência. Falhou: evidência, documentação oficial, ajuste, ADR, repetir. Não avançar sem GNG-1.

### Fase 1 — Fundação determinística [v0.2]
Escopo: Tenant, Organização, Projeto, Usuário local, Conversa, Solicitação, Demanda, Tarefa, Tentativa; eventos internos; ledger; Outbox/Inbox; leases/heartbeats/fencing; checkpoints; `IDurableExecutionEngine` completo; WorkflowDefinition/Run/Fases/Gates; Documentos com versões e aprovações; workers (watchdog, outbox dispatcher, reconciliador); hub SignalR; persistência dual com testes nos dois providers. Testes: unit, integração dual, concorrência, recuperação automatizada (`Harness.RecoveryTests`), arquitetura. Saída (**GNG-2**): tarefa em andamento sobrevive a encerramento abrupto e é reconciliada sem perda/duplicação, com auditoria completa. Intercalável: contratos/OpenAPI dos recursos da seção 4.18.

### Fase 2 — MVP pessoal: API completa + integração do frontend [v0.3]
Escopo: **API e SignalR de todas as funcionalidades das telas**: organizações, projetos, cockpit/digest, chat com streaming, conversas, workflows e definições versionadas, quadro, documentos, central de aprovações/decisões, orquestrador, equipe, governança/auditoria, prototipação, catálogo de ferramentas/skills/plugins/MCP, rodar projeto mínimo [.NET e Node], notificações, providers/contas, licenças/entitlements, configurações e perfil local.

Implementar seis definições de agente; chefe operando o pipeline completo com `CodexCliAgentExecutor` em sandbox; progresso executado/validado/aprovado; modos manual e semiautônomo.

**Integração no mesmo monorepo:** detectar `frontend/package.json`; executar instalação reproduzível pelo lockfile e `npm run build`; servir o resultado pelo `Harness.Host`; em desenvolvimento, permitir Vite separado com proxy para a API; executar os E2E do frontend contra a API real e reconciliar OpenAPI, eventos e `docs/frontend/HANDOFF_API.md`.

Não copiar código de outro repositório e não mover `frontend/`. Se o frontend ainda não estiver pronto, usar gate provisório com testes de contrato da API e smoke via API, prosseguindo sem inventar telas. Testes: integração de API por recurso, contrato, smoke manual com Codex real documentado. Saída (**GNG-3**, dogfood): o Harness entrega uma feature real de ponta a ponta em repositório real, com auditoria completa da solicitação ao merge, usando a UI integrada; validação humana registrada (trabalho independente pode continuar enquanto o aceite estiver pendente).

### Fase 3 — Workflows completos e modo autônomo [v0.4]
Workflows Triagem->Sustentação; variantes (novo, bug, evolução, legado, engenharia reversa); modo autônomo com política; **bloqueios invioláveis mesmo em modo autônomo**: deploy em produção, exclusão de dados, alteração de política, push em branch protegida, gasto acima do budget, alteração de regra de negócio sem requisito aprovado, credencial não autorizada; verificação de consistência (determinística -> estrutural -> LLM); troca de modo com aceite de risco. Testes: máquina de estados de gates (property-based onde couber); testes de burla das ações bloqueadas devem falhar. Saída: workflow completo em semiautônomo + bloqueios comprovados.

### Fase 4 — PO Assistant reduzido [v0.5]
Ingestão de texto e anexos (documentos, imagens, PDFs, planilhas, ZIPs); extração de requisitos, contradições, ambiguidades, perguntas, critérios de aceite; criação de demanda estruturada. Segurança de upload: tipo, tamanho, hash, anti zip-bomb, path traversal, bloqueio de executável, quarentena. Saída: uploads maliciosos de teste rejeitados com evidência; demanda criada de documento real.

### Fase 5 — Prototipação [v0.5]
Referências por organização/projeto; upload de imagens e ZIP (nunca executado); galeria; histórico; marca; waiver/não aplicável; comparação protótipo x implementação (v1 manual). Saída: três cenários registrando decisão no projeto.

### Fase 6 — Rodar projeto completo [v0.6]
Detecção em camadas (manifestos -> arquivos de projeto -> Dockerfiles -> Compose -> scripts registrados -> heurísticas -> agente como último recurso); stacks .NET, React, Node.js, Python, Java; serviços, URLs, health, logs, banco, portas, credenciais demo protegidas; start/stop/restart/cleanup (regras 1.2). Saída: três stacks diferentes iniciadas e paradas via API, com logs e health.

### Fase 7 — Empacotamento desktop [v0.7]
Launcher; backend+worker+**frontend buildado embarcado**; SQLite; abertura no navegador; atualizações; backup/restore; diagnóstico; logs; desinstalação segura. Sem Tauri/Electron sem ADR. Self-contained. Saída (parte do GNG-4): instalação em macOS limpo, sem IDE, ponta a ponta.

### Fase 8 — Licenciamento individual [v0.8]
Licença por instalação/usuário; documento assinado (Ed25519); entitlements; expiração; grace; offline; ativação; revogação; exportação de dados; auditoria. **Nunca bloquear acesso aos dados após expiração.** Saída (**GNG-4**): máquina limpa + licença offline + expiração simulada mantendo leitura/exportação.

### Fase 9 — Canais externos [v0.9 parcial]
Ordem: terminal -> Telegram -> Teams. Linking explícito de identidade; Inbox idempotente; resposta no canal de origem; deduplicação; retentativa; limites do provider; anexos; auditoria. Saída: conversa terminal->Telegram com webhook reentregue de propósito e zero duplicação.

### Fase 10 — Modo servidor multiusuário [v0.9]
Até 30 usuários simultâneos; PostgreSQL; OIDC/Entra ID; RBAC+ABAC; organizações; isolamento por projeto; runners concorrentes; auditoria; backup/restore; rate limit; budgets. Sem Kubernetes, Orleans, RabbitMQ ou Temporal sem medição registrada em ADR. Testes: carga 30 usuários realista; isolamento adversarial. Saída (**GNG-5**): carga verde; zero violação de isolamento; failover de runner.

### Fase 11 — Hardening e release 1.0 [v1.0]
Threat modeling formal; SAST; dependency/secret scanning; SBOM; testes de isolamento, recuperação, upgrade, licença, migração SQLite->PostgreSQL, carga, E2E, regressão; backup/restore; documentação de instalação/operação; runbook de incidentes. Saída (**GNG-6**): checklist de hardening + DoD global completos.

## 6. Ordem de execução, marcos e gates

Caminho crítico: **F0 -> F1 -> F2 -> F3 -> F10 -> F11**. F4/F5/F6 intercaláveis após F2 (ordem F6 -> F4 -> F5). F9 (terminal) após F2; Telegram após F7. F7 -> F8 após F6. "Paralelizável" = intercalável em fatias pequenas: uma fatia por vez, conclua-commite-atualize antes de trocar; um épico nunca é uma fatia.

Estimativas (dias de execução efetiva; metas internas, não compromissos — nunca reduza testes/segurança/evidência por prazo): F0 3/5/8 · F1 6/10/16 · F2 8/14/22 · F3 5/8/13 · F4 3/5/8 · F5 2/4/6 · F6 3/5/8 · F7 3/5/8 · F8 2/4/6 · F9 4/7/11 · F10 6/10/16 · F11 5/8/13 · **total 50/85/135**.

Gates: GNG-1 (9 PoCs), GNG-2 (kill -9 reconciliado), GNG-3 (dogfood integrado; única homologação humana bloqueante — tarefas independentes continuam), GNG-4 (instalação limpa + licença offline + dados pós-expiração), GNG-5 (carga + isolamento + failover), GNG-6 (hardening + DoD). Gate verde: registrar evidência em `PROGRESS.md`, commit, continuar automaticamente. No-Go: parar só o caminho dependente, causa raiz em `RISKS.md`, corrigir (ADR se arquitetural), repetir. Não contornar gate.

## 7. Riscos com potencial de mudança arquitetural (gatilho -> resposta)

1. Contenção de escrita SQLite -> consolidar escritas no dispatcher; persistindo, Postgres local em container `harness-` como opção do modo pessoal (ADR).
2. Codex CLI instável para automação (PoC-4) -> wrapper com checkpoints menores e retomada por commit; alternativas: promover `MicrosoftAgentFrameworkExecutor` ou `OmpRpcAgentExecutor` a executor principal (ADR com medição).
3. Egress allowlist não confiável no Colima/Docker (PoC-6) -> proxy obrigatório com deny default; limitação residual documentada como risco aceito.
4. Limite de contexto do próprio agente -> fatias e catálogos mais granulares.
5. Structured output do chefe instável -> retry com repair; simplificar schema; fixar provider por configuração.
6. Conflito de porta/certificado local -> porta dinâmica, HTTP somente em loopback, launcher resolve.
7. Drift de contrato com o frontend -> OpenAPI canônico publicado a cada mudança; reconciliação registrada na integração; testes de contrato no pipeline.

## 8. Backlog épico

EP-01 Bootstrap/pipeline; EP-02 SharedKernel/contratos; EP-03 Persistência dual; EP-04 Ledger/Outbox/Inbox; EP-05 Motor durável; EP-06 Identity/Organizations/Projects; EP-07 Conversations/gateway; EP-08 Chefe; EP-09 Cadeia Solicitação->Tentativa; EP-10 Workflows/gates/progresso; EP-11 Runner+Sandbox+CodexCliAgentExecutor; EP-12 Operações Git; EP-13 Contrato API/SignalR + OpenAPI; EP-14 Integração do frontend; EP-15 Rodar projeto; EP-16 PO Assistant; EP-17 Prototipação; EP-18 Empacotamento; EP-19 Licenciamento; EP-20 Canais; EP-21 Servidor multiusuário; EP-22 Hardening/release. Backlog vivo em `MASTER_PLAN.md`.

## 9. Qualidade obrigatória

Backend: unit, integration (dual provider), architecture, contract, recovery e concurrency tests; mutation testing nos módulos críticos quando viável. Segurança: nenhuma vulnerabilidade crítica conhecida; nenhum segredo em repositório; nenhum comando de agente fora de política; nenhuma escrita com fencing expirado; nenhuma mensagem processada duas vezes. Pipeline local roda tudo em um comando. E, sempre, seção 4.17: concluído = executado com evidência.

## 10. Autonomia, comunicação e continuidade

Não interrompa por: nomes internos, estrutura de pastas, pequenas decisões, refatorações, testes, correções, dependências comuns. Dúvida não bloqueadora: suposição em `DECISIONS_PENDING.md`, alternativa mais segura, prosseguir, ADR se arquitetural. Interrompa somente por: credencial indisponível, decisão jurídica, compra, conta externa, publicação remota, acesso não substituível por fake, ação destrutiva fora do workspace. Fim de sessão: working tree consistente, testes executados, commit, `CURRENT_STATE.md` com próximo passo exato, sem migrations parciais, sem processos órfãos, sem worktree fora do catálogo. Início de sessão: ler `CURRENT_STATE.md`, verificar Git/processos/banco/worktrees/containers, sanidade, retomar.

## 11. Definition of Done global

Concluído somente quando, comprovadamente: instala em máquina limpa e inicia sem IDE; cria projeto e registra repositório; recebe solicitação e cria demanda e tarefas; executa agente Codex em ambiente isolado; cria branch e worktree; produz código; executa build e testes; solicita critic; aplica gates; atualiza o quadro em tempo real via SignalR; sobrevive a encerramento abrupto e retoma sem perda; exibe auditoria completa; roda o projeto produzido; gera documentação; faz backup e restore; opera em modo pessoal e em modo servidor com até 30 usuários; passa nos testes de segurança e recuperação; possui instalador, documentação e licenciamento individual; serve o frontend integrado a partir de `frontend/`; mantém OpenAPI/eventos reconciliados com os contratos da Kimi; respeita a política exclusiva `main`/`develop`; e não depende do histórico de nenhuma sessão de IA para continuar funcionando.

## 12. Início imediato

1. Ler este prompt integralmente.
2. Garantir o diretório `$POSEIDON_FRONTEND_CLONE`.
3. Clonar o repositório oficial se ele ainda não existir; se existir, validar raiz e remote.
4. Materializar `main` e `develop` conforme a seção 1.1, sem criar qualquer terceira branch.
5. Trabalhar exclusivamente em `develop`.
6. Aplicar o contrato de idempotência ao workspace.
7. Verificar se há outra agente usando a mesma working tree; não editar simultaneamente.
8. Inventariar ambiente e Docker conforme a seção 1.2 em `ENVIRONMENT.md`.
9. Ler e preservar `frontend/**` e `docs/frontend/**`; usar apenas como contrato e integração.
10. Validar `CURRENT_STATE.md` contra Git, banco, containers, processos e testes; retomar do primeiro incremento incompleto.
11. Criar ou atualizar documentação viva sem duplicar conteúdo válido.
12. Criar ou validar a solução conforme 4.1, mantendo intacto o frontend.
13. Criar ou validar ADRs.
14. Executar apenas as 9 PoCs ainda não comprovadas, cada uma com evidência e commit.
15. Antes de cada push em `develop`, buscar e reconciliar `origin/develop`, executar os testes relacionados e nunca usar force push.
16. GNG-1 verde: registrar e iniciar Fase 1; vermelho: corrigir e repetir.
17. Prosseguir pelas fases até concluir ou encontrar bloqueio humano legítimo.
18. Nunca fazer merge em `main` sem autorização humana explícita.

Comece imediatamente e mantenha o projeto sempre retomável.

ADENDO — EXECUÇÃO PARALELA COM O KIMI
Este adendo prevalece sobre qualquer instrução anterior incompatível relacionada ao diretório local e à execução simultânea.
O Kimi está trabalhando atualmente em:
$POSEIDON_FRONTEND_CLONE
Ele já concluiu o FE-0 e o FE-1a e está executando o FE-1b. Portanto, você não deve aguardar a conclusão do frontend.
1. Clone de trabalho exclusivo do backend
Para evitar que dois agentes alterem simultaneamente a mesma working tree, execute o backend em um segundo clone:
$REPO_ROOT
Procedimento:
1. Não altere a pasta $POSEIDON_FRONTEND_CLONE, que está sendo usada pelo Kimi.
2. Clone https://github.com/mateusdomi/harness-poseidon.git em $REPO_ROOT.
3. Faça checkout da branch develop.
4. Confirme que o remote origin aponta para o repositório oficial.
5. Não crie nenhuma branch adicional.
6. Todo o trabalho continua sendo feito e publicado em develop.
Os dois diretórios são clones do mesmo repositório; não representam projetos ou branches diferentes.
2. Propriedade de arquivos durante a execução paralela
Enquanto o Kimi estiver ativo, você pode alterar somente:
* src/**
* tests/**
* infra/**
* tools/backend/**
* docs/backend/**
* docs/contracts/**
* docs/architecture/**
* docs/decisions/**
* docs/security/**
* docs/testing/**
* Arquivos .sln, .csproj, Directory.Build.*, Directory.Packages.props e global.json
Você não pode alterar:
* frontend/**
* docs/frontend/**
Enquanto o frontend estiver em desenvolvimento, evite também alterar estes arquivos compartilhados, salvo necessidade técnica comprovada:
* README.md
* .gitignore
* Pipelines de CI/CD na raiz
* Arquivos JavaScript ou Node da raiz
* Configurações pertencentes ao frontend
Quando uma alteração compartilhada for necessária, registre-a em:
docs/backend/execution/DECISIONS_PENDING.md
e adie a alteração até sincronizar o trabalho mais recente do Kimi.
3. Sincronização obrigatória antes de cada push
Antes de cada push em develop:
1. Garanta que sua working tree está commitada.
2. Execute git fetch origin.
3. Execute git rebase origin/develop.
4. Preserve integralmente as alterações provenientes do Kimi.
5. Execute novamente os builds e testes afetados.
6. Faça push normal para develop.
7. Nunca use --force ou --force-with-lease.
Caso o push seja rejeitado porque o Kimi publicou mudanças:
1. Execute novo git fetch origin.
2. Faça rebase sobre origin/develop.
3. Execute novamente os testes.
4. Tente o push novamente.
4. Tratamento de conflitos
Você não pode resolver conflitos removendo ou substituindo trabalho do Kimi.
Se surgir conflito em:
* frontend/**
* docs/frontend/**
preserve a versão de origin/develop, pois esses arquivos pertencem ao Kimi.
Se surgir conflito em arquivo compartilhado:
1. Analise as duas versões.
2. Preserve os dois comportamentos válidos.
3. Não descarte conteúdo silenciosamente.
4. Registre a resolução em docs/contracts/CONTRACT_RECONCILIATION.md ou ADR, conforme o caso.
5. Execute os testes antes do push.
Se não for possível reconciliar com segurança, interrompa apenas o push, mantenha o trabalho backend commitado localmente e aguarde a próxima versão estável de origin/develop.
5. Contrato provisório durante o desenvolvimento
O frontend ainda não está completo.
Você deve iniciar imediatamente pelas fases de backend e pelas PoCs que não dependem da interface.
Durante esse período:
* Leia os contratos já publicados em frontend/src/api/contracts/**, quando existirem.
* Leia docs/frontend/HANDOFF_API.md, quando existir.
* Trate os contratos atuais como provisórios.
* Gere normalmente docs/contracts/openapi.json e docs/contracts/events.json.
* Registre divergências em docs/contracts/CONTRACT_RECONCILIATION.md.
* Não altere o frontend para fazê-lo se adaptar ao backend.
* Não bloqueie a fundação backend por ausência de uma tela ou contrato ainda não produzido pelo Kimi.
A integração real do frontend deve ocorrer quando as respectivas telas e contratos estiverem disponíveis em origin/develop.
6. Trabalho permitido imediatamente
Comece agora pelas atividades independentes do frontend:
* Inventário do ambiente e Docker.
* Bootstrap da solução .NET.
* SharedKernel.
* Testes de arquitetura.
* Persistência SQLite e PostgreSQL.
* Dispatcher único de escrita.
* IPC entre Host e Runner.
* Ledger, Inbox e Outbox.
* Leases, fencing tokens e heartbeats.
* Motor de execução durável.
* Sandbox.
* Runner.
* CodexCliAgentExecutor.
* Operações Git em repositórios fixture.
* SignalR e envelope de eventos.
* OpenAPI.
* PoCs da Fase 0.
* Fundação determinística da Fase 1.
Não espere o Kimi para começar essas atividades.
7. Integração posterior
Quando o Kimi publicar novos gates em develop:
1. Faça fetch e rebase.
2. Execute o build do frontend sem modificar seu código.
3. Compare contratos mockados com OpenAPI e eventos reais.
4. Implemente os endpoints ainda ausentes.
5. Execute os testes de integração.
6. Registre divergências resolvidas.
7. Continue o backend normalmente.
A pasta $REPO_ROOT pode permanecer como clone de trabalho do Codex até o fim do desenvolvimento. A fonte oficial continuará sendo a branch remota develop.
Comece imediatamente e não aguarde a conclusão do frontend.
