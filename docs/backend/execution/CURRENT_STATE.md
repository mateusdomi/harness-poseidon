# Estado atual do backend

Atualizado em: 2026-07-18T13:37:34Z

## Retomada rápida

- Fase atual: Fase 1 — Fundação determinística; GNG-1 verde com 9/9 PoCs.
- Épico atual: EP-04 — processamento transacional de Inbox/Outbox e ledger; esquema EP-03 verde.
- Branch obrigatória: `develop`.
- Último commit remoto validado: `ac76408` (`develop`); a fatia de schema F1 está verde e aguardando o commit que conterá este estado.
- Próximo passo exato: criar contratos de comando/receipt em Persistence.Abstractions e implementar uma transação idempotente comum que grava Inbox, mutação de estado, ledger hash-encadeado e Outbox nos dois providers; testar replay/conflito/rollback antes de mover o store IPC in-memory.
- Bloqueios: nenhum.

## Suposições ativas

- O conteúdo integral v1.3 fornecido pelo usuário é a única fonte de verdade. O arquivo `PROMPT_CODEX_BACKEND_HARNESS_POSEIDON_v1.3.md` não foi encontrado no filesystem.
- O clone `/Users/mateus/Documents/harness-poseidon` está ativo com alterações não commitadas da Kimi e é somente leitura para o trabalho backend.
- O trabalho Codex ocorre exclusivamente em `/Users/mateus/Documents/harness-poseidon-backend`.
- Contratos em `frontend/src/api/contracts/**` e `docs/frontend/HANDOFF_API.md` são provisórios até reconciliação; não serão editados pelo backend.

## Estado persistido e operacional

- Banco de dados: nenhum persistente no workspace; bancos temporários SQLite e containers/volumes PostgreSQL das PoCs foram removidos após os testes.
- Migrations: SQLite possui `0001_foundation.sql` sob dispatcher único; PostgreSQL possui `0001_poc_work_queue.sql` e `0002_foundation.sql` sob advisory lock. Históricos são separados e idempotentes; não há migration parcialmente aplicada.
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
- IPC: Runner real envia heartbeat/checkpoint/conclusão a endpoint loopback autenticado; replay integral não duplica, gap/token inválido não criam estado e assembly Runner não referencia banco. Store da PoC é in-memory e será persistido na primeira fatia F1.
- Fundação F1: sete tabelas conceituais (Tenant, Organização, Projeto, usuário local, Inbox, Outbox, ledger) existem nos dois providers; migrations repetidas são no-op e FKs órfãs são rejeitadas.
- Pipeline: `tools/backend/verify.sh` verde após o schema F1: restore locked, format, build Release com zero warnings/erros e 50/50 testes verdes.
- Host smoke: `/health` respondeu `{"status":"healthy"}` em porta loopback dinâmica 53906; processo finalizado com exit code 0.
- Evidências: PoCs 1–9 e schema dual F1 verdes/catalogados; GNG-1 verde. GNG-2 permanece fechado até a recuperação F1 com auditoria completa.

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

O SDK local esperado é 10.0.302. Se estiver ausente, executar `tools/backend/install-dotnet.sh`; se estiver válido, executar `tools/backend/verify.sh` e retomar pelo modelo relacional F1 descrito no próximo passo.
