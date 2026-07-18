# Estado atual do backend

Atualizado em: 2026-07-18T15:44:37Z

## Retomada rápida

- Fase atual: Fase 1 — Fundação determinística; GNG-1 verde com 9/9 PoCs.
- Épico atual: EP-10b.2 — store transacional dual-provider de workflow; contrato e schema estão verdes.
- Branch obrigatória: `develop`.
- Último commit remoto validado: `0e4b4a6` (`develop`); schema EP-10b.1 está verde e aguarda o commit que conterá este estado.
- Próximo passo exato: definir `IWorkflowStore` e comandos provider-neutral para criar/publicar definição e iniciar/avançar run; implementar primeiro a criação/publicação atômica com Inbox, ledger e Outbox nos dois providers, seguida das mutações optimistic-concurrency e reidratação.
- Bloqueios: nenhum.

## Suposições ativas

- O conteúdo integral v1.3 fornecido pelo usuário é a única fonte de verdade. O arquivo `PROMPT_CODEX_BACKEND_HARNESS_POSEIDON_v1.3.md` não foi encontrado no filesystem.
- O clone `/Users/mateus/Documents/harness-poseidon` está ativo com alterações não commitadas da Kimi e é somente leitura para o trabalho backend.
- O trabalho Codex ocorre exclusivamente em `/Users/mateus/Documents/harness-poseidon-backend`.
- Contratos em `frontend/src/api/contracts/**` e `docs/frontend/HANDOFF_API.md` são provisórios até reconciliação; não serão editados pelo backend.
- `runner_attempts` é uma projeção de transporte do IPC, não o agregado de domínio Tentativa; o EP-05 deve ligá-la à tentativa durável/tenant sem permitir ao Runner criar autoridade de domínio.

## Estado persistido e operacional

- Banco de dados: nenhum persistente no workspace; bancos temporários SQLite e containers/volumes PostgreSQL das PoCs foram removidos após os testes.
- Migrations: SQLite possui fundação, Runner IPC, execução durável, cadeia e workflows; PostgreSQL possui também a PoC queue e as mesmas áreas em SQL próprio. Históricos são separados/idempotentes (`5→0` e `6→0`); não há migration parcialmente aplicada.
- Worktrees vinculadas a este clone: somente a raiz em `develop`; nenhuma worktree adicional.
- Branches locais/remotas observadas: somente `main` e `develop`.
- Processos `Harness.Host`, `Harness.Runner` ou `Harness.Launcher`: nenhum.
- Containers em execução: nenhum do Harness; os 11 containers de terceiros permanecem parados.
- Recursos Docker com `com.harness.managed=true`: nenhum container, volume ou network.
- Solução: 22 projetos de produção (Host, Runner, Launcher, SharedKernel, persistência e 15 módulos) e 6 projetos de teste em `Harness.sln`.
- SharedKernel: ULID canônico, `EntityId<TTag>`, `IClock`, `SystemClock`, `ErrorDescriptor` e `Result`/`Result<T>` implementados.
- SQLite: EF Core SQLite 10.0.10; native SQLite pinado em 3.53.3 por segurança; dispatcher único validado em WAL.
- Recuperação: processo fixture sofreu SIGKILL real após 3/6 checkpoints; nova instância reconciliou e concluiu com 6 checkpoints únicos.
- Fencing: token antigo não gravou nem renovou após aquisição do token crescente pelo novo owner.
- Codex CLI: app-server real supervisionado com ambiente/estado isolados; heartbeat crescente, kill da árvore, retomada por `threadId` e sessão nova reidratada do commit Git, sem turno de modelo.
- Git/claims: três fixtures criaram duas branches/worktrees de tentativa; claims disjuntos executaram em paralelo e claim ancestral bloqueou conflito; refs/worktrees oficiais ficaram idênticas antes/depois.
- Sandbox: Docker provider validou CPU 0,5, memória 64 MiB, 64 PIDs, disk limit 8 MiB, worktree montada, proxy-only egress, rootfs read-only e cleanup label-guarded em seis execuções verdes.
- Realtime: hub `/hubs/events`, sequência por stream, catálogo tipado, endpoint snapshot+delta e OpenAPI determinístico; lacuna 3–5 recuperada e live retomado em 6.
- PostgreSQL: Npgsql/EF provider 10.0.3; 80 itens adquiridos uma vez por 12 workers, linha bloqueada pulada sem espera, token antigo rejeitado após lease expirada e migrations `1` depois `0`; imagem final Alpine/PostgreSQL 18.4 passou Scout com 0 crítica/alta/média e residual 2 baixas + 1 não classificada sem correção disponível.
- IPC: Runner real envia heartbeat/checkpoint/conclusão a endpoint loopback autenticado; o Host persiste tentativa, versão, sequência, checkpoints, Inbox e Outbox via `IRunnerMessageStore`. Replay integral depois de reiniciar o Host não duplica estado/eventos; gap, owner conflitante, chave conflitante, tentativa concluída e token inválido são rejeitados. O assembly Runner continua sem referência a persistência.
- Fundação F1: sete tabelas conceituais (Tenant, Organização, Projeto, usuário local, Inbox, Outbox, ledger) existem nos dois providers; migrations repetidas são no-op e FKs órfãs são rejeitadas.
- Transação F1: contratos comuns provisionam Tenant→Projeto e gravam Inbox, ledger SHA-256 e Outbox atomicamente; 10 concorrentes resultam 1 aplicação/9 replays em ambos providers, conflito de hash e colisão Outbox não deixam efeitos.
- Motor durável: contrato/schema/borda comuns e adapters completos verdes. `SqliteDurableExecutionEngine` usa o dispatcher único; `PostgresDurableExecutionEngine` usa locks de linha/transacionais e `FOR UPDATE SKIP LOCKED`. O mesmo cenário provider-neutral comprovou Inbox, lifecycle, aquisição concorrente, fencing, checkpoint, retry, timer/sinal e reconciliação nos dois providers.
- Recuperação GNG-2: subprocessos reais SQLite e PostgreSQL receberam `SIGKILL` após 3/6 checkpoints; restart/reconciliação abandonou attempt 1, criou attempt 2 com fencing maior, retomou do checkpoint 3 e concluiu 6/6. Cada provider comprovou 2 attempts, 6 checkpoints, 8 receipts de Inbox, 6 transições/Outbox e ledger encadeado de 7 eventos. O critério técnico está verde; a saída formal da Fase 1 aguarda o restante do escopo funcional.
- Auditoria: toda transição do motor agora anexa o ledger global na mesma transação. O hash canonicaliza objetos JSON recursivamente para permanecer verificável após normalização `jsonb`; PostgreSQL serializa a cadeia por tenant.
- Cadeia EP-09a: `WorkChainAggregate` em Coordination materializa Solicitação/Demanda/Tarefa/Instrução/Tentativa/Revisão. Solicitação e instrução são append-only; hash SHA-256 é calculado pelo domínio; somente a instrução mais recente inicia tentativa; há uma única tentativa ativa; completion exige evidência; rejeição exige nova versão; actor–critic é obrigatório a partir de risco médio. Execution permanece responsável pela tentativa técnica/lease, separada da tentativa de negócio.
- Schema EP-09b.1: migrations SQLite `0004_work_chain` e PostgreSQL `0005_work_chain` criam Solicitação, Demanda, Tarefa, Instrução, Tentativa, Evidência e Revisão com FKs compostas por tenant/projeto, checks, versões únicas e índice parcial de tentativa ativa. Cadeia válida foi inserida e segunda tentativa `running` foi rejeitada nos dois providers.
- Store EP-09b.2a: `IWorkChainStore` possui criação e snapshot. Validator comum exige ULIDs, critérios não vazios, risk tier, peso e hash da instrução. SQLite usa dispatcher; PostgreSQL usa advisory locks para idempotência e ledger. Estado inicial, Inbox, ledger e Outbox commitam juntos. Dez comandos concorrentes resultam 1 aplicação/9 replays; chave conflitante não altera snapshot.
- Store EP-09b.2b.1: start/complete/review usam versão esperada e Inbox idempotente. SQLite serializa no dispatcher; PostgreSQL usa advisory lock por tarefa e `FOR UPDATE`. Estado, ledger e Outbox commitam juntos. Partida concorrente resulta 1 aplicação/1 replay; versão antiga não muta; completion persiste evidência; autoaprovação de risco médio é recusada; critic independente conclui a tarefa. Snapshot final comprova versão 4 e contagens 1/1/1.
- Store EP-09b.2b.2: review rejeitado exige nova instrução. Correção imutável cria v2 com `supersedesId=v1`, versão otimista, Inbox, ledger e Outbox; tentativa 2 conclui e é aprovada. Leitura transacional completa reidrata Solicitação→Demandas→Tarefas→todas as instruções/tentativas/evidências/reviews. Comportamento final nos dois providers: tarefa v8, 2 instruções, 2 tentativas, 2 evidências e 2 reviews.
- Workflow EP-10a: definições tipadas possuem versões imutáveis/hash/publicação; runs aceitam somente versão publicada, mantêm uma fase ativa, itens monotônicos e gates não contornáveis. Progresso executado/validado/aprovado é recomputado dos pesos e estados; 9 cenários cobrem validação, lifecycle, gate/retry, pausa e conclusão 100/100/100.
- Workflow schema EP-10b.1: migrations SQLite `0005_workflows` e PostgreSQL `0006_workflows` criam 10 tabelas de definição/run com FKs compostas, estados fechados, pesos positivos, publicação/lifecycle coerentes e índice parcial de uma fase ativa. Cadeia válida foi inserida e segunda fase ativa foi rejeitada nos dois providers; migrations `5→0`/`6→0`.
- Migrations: SQLite `5→0` e PostgreSQL `6→0`, idempotentes e sem estado parcial.
- Pipeline: `tools/backend/verify.sh` verde após schema EP-10b.1: restore locked, format, build Release com zero warnings/erros e 83/83 testes verdes.
- Host smoke: `/health` respondeu `{"status":"healthy"}` em porta loopback dinâmica 53906; processo finalizado com exit code 0.
- Evidências: PoCs 1–9, fundação dual, IPC relacional, motor durável, prova abrupta, cadeia Solicitação→Revisão e contrato/schema de workflow verdes/catalogados; GNG-1 verde. O critério de recuperação do GNG-2 está comprovado, mas a Fase 1 permanece aberta para store EP-10, documentos e workers.

## Sanidade antes de retomar

```bash
cd /Users/mateus/Documents/harness-poseidon-backend
git status --short
git branch --show-current
git remote get-url origin
git fetch origin
git log --oneline --decorate -5
git worktree list
docker ps -a --filter label=com.harness.managed=true
docker volume ls --filter label=com.harness.managed=true
docker network ls --filter label=com.harness.managed=true
tools/backend/dotnet.sh --info
```

O SDK local esperado é 10.0.302. Se estiver ausente, executar `tools/backend/install-dotnet.sh`; se estiver válido, executar `tools/backend/verify.sh` e retomar pelos agregados descritos no próximo passo.
