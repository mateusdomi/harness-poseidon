# Estado atual do backend

Atualizado em: 2026-07-18T14:13:19Z

## Retomada rápida

- Fase atual: Fase 1 — Fundação determinística; GNG-1 verde com 9/9 PoCs.
- Épico atual: EP-05 — motor durável; IPC sobre autoridade relacional está verde.
- Branch obrigatória: `develop`.
- Último commit remoto validado: `c1fb600` (`develop`); a borda comum de validação EP-05c está verde e aguardando o commit que conterá este estado.
- Próximo passo exato: implementar Start/TryAcquire/Renew/Heartbeat/Checkpoint primeiro em `SqliteDurableExecutionEngine`, usando o dispatcher e Inbox/transição/Outbox na mesma transação; extrair o teste comportamental e repetir em PostgreSQL com `FOR UPDATE SKIP LOCKED`.
- Bloqueios: nenhum.

## Suposições ativas

- O conteúdo integral v1.3 fornecido pelo usuário é a única fonte de verdade. O arquivo `PROMPT_CODEX_BACKEND_HARNESS_POSEIDON_v1.3.md` não foi encontrado no filesystem.
- O clone `/Users/mateus/Documents/harness-poseidon` está ativo com alterações não commitadas da Kimi e é somente leitura para o trabalho backend.
- O trabalho Codex ocorre exclusivamente em `/Users/mateus/Documents/harness-poseidon-backend`.
- Contratos em `frontend/src/api/contracts/**` e `docs/frontend/HANDOFF_API.md` são provisórios até reconciliação; não serão editados pelo backend.
- `runner_attempts` é uma projeção de transporte do IPC, não o agregado de domínio Tentativa; o EP-05 deve ligá-la à tentativa durável/tenant sem permitir ao Runner criar autoridade de domínio.

## Estado persistido e operacional

- Banco de dados: nenhum persistente no workspace; bancos temporários SQLite e containers/volumes PostgreSQL das PoCs foram removidos após os testes.
- Migrations: SQLite possui fundação, Runner IPC e `0003_durable_execution.sql`; PostgreSQL possui PoC queue, fundação, Runner IPC e `0004_durable_execution.sql`. Históricos são separados/idempotentes (`3→0` e `4→0`); não há migration parcialmente aplicada.
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
- Motor durável: `IDurableExecutionEngine` cobre lifecycle, sinal, timer, lease/fencing, heartbeat, checkpoint, retry, dead-letter, reconciliação e consulta; matriz de transições e backoff determinístico/capado estão verdes. Nove tabelas duráveis existem nos dois providers. Codec de estados, validação ULID/JSON/bounds/lease e hash SHA-256 são compartilhados; implementação de comandos ainda pendente.
- Pipeline: `tools/backend/verify.sh` verde após a borda EP-05c: restore locked, format, build Release com zero warnings/erros e 61/61 testes verdes.
- Host smoke: `/health` respondeu `{"status":"healthy"}` em porta loopback dinâmica 53906; processo finalizado com exit code 0.
- Evidências: PoCs 1–9, fundação dual, IPC relacional, contrato e schema do motor durável verdes/catalogados; GNG-1 verde. GNG-2 permanece fechado até as implementações do motor e a recuperação F1 com auditoria completa.

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

O SDK local esperado é 10.0.302. Se estiver ausente, executar `tools/backend/install-dotnet.sh`; se estiver válido, executar `tools/backend/verify.sh` e retomar pelo EP-05 descrito no próximo passo.
