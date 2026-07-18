# Progresso e evidências

## Estado dos gates

| Gate | Critério resumido | Estado | Evidência |
|---|---|---|---|
| GNG-1 | nove PoCs verdes | verde | PoCs 1–9 executadas e comprovadas; pipeline 49/49 |
| GNG-2 | recuperação abrupta com auditoria completa | fechado | F1 não iniciada |
| GNG-3 | dogfood integrado com validação humana | fechado | F2 não iniciada |
| GNG-4 | instalação limpa e licença offline | fechado | F7/F8 não iniciadas |
| GNG-5 | carga, isolamento e failover | fechado | F10 não iniciada |
| GNG-6 | hardening e DoD global | fechado | F11 não iniciada |

## Fase 0

| Incremento | Estado | Última evidência |
|---|---|---|
| Inventário de ambiente/Docker | executado | comandos concluídos com exit code 0 em 2026-07-18; 11 containers parados, 14 volumes, 7 networks, zero recursos Harness |
| SDK .NET local | executado e validado | SDK 10.0.302 e runtimes 10.0.10; `dotnet --info` exit code 0 em 2026-07-18 |
| Bootstrap/pipeline | executado e validado | `tools/backend/verify.sh`: exit 0, build Release 0 warnings/0 errors, 10 testes verdes em 2026-07-18 |
| Estrutura/arquitetura | executado e validado | 28 projetos, 28 lockfiles; 5 testes de arquitetura verdes, incluindo ArchUnitNET 0.13.3 |
| Host mínimo/porta dinâmica | executado e validado | Host ouviu em `127.0.0.1:53906`, `/health` retornou `{"status":"healthy"}`, shutdown exit 0 |
| SharedKernel/contratos primitivos | executado e validado | 23 testes unitários; suíte completa com 32 testes, build 0 warnings/0 errors em 2026-07-18 |
| PoC-1 SQLite/dispatcher | verde | 960 writes, 24 produtores, single reader, WAL/FK/5 s busy timeout, 5 repetições; `evidence/F0-POC-1.md` |
| PoC-2 retomada | verde | SIGKILL após 3/6 checkpoints; restart, 1 reconciliação, 2 tentativas, 6 checkpoints únicos; `evidence/F0-POC-2.md` |
| PoC-3 lease/fencing | verde | owner A token 1 expirado, owner B token 2; write/renew antigos rejeitados; `evidence/F0-POC-3.md` |
| PoC-4 Codex CLI | verde | subprocesso real, heartbeat, kill da árvore, retomada por `threadId` e reidratação Git; `evidence/F0-POC-4.md` |
| PoC-5 Git fixtures/claims | verde | três fixtures, branches/worktrees paralelas, claims com/sem interseção, Harness intacto; `evidence/F0-POC-5.md` |
| PoC-6 sandbox | verde | limites CPU/memória/PIDs/disco, worktree, proxy-only egress, cleanup label-guarded; `evidence/F0-POC-6.md` |
| PoC-7 SignalR | verde | envelope sequenciado, desconexão, delta `[3,4,5]`, retomada na sequência 6 e drift tests; `evidence/F0-POC-7.md` |
| PoC-8 PostgreSQL | verde | migration própria idempotente, 80 itens/12 workers sem duplicação, `SKIP LOCKED`, fencing e imagem sem vulnerabilidade crítica/alta/média; `evidence/F0-POC-8.md` |
| PoC-9 IPC | verde | Runner real, loopback+token, heartbeat/checkpoint/completion, replay 3/3, gap e token inválido rejeitados; `evidence/F0-POC-9.md` |

Conclusão só será registrada após execução. Arquivo existente ou teste apenas escrito não conta como evidência.

## Fase 1

| Incremento | Estado | Última evidência |
|---|---|---|
| Esquema relacional dual-provider | verde | migrations próprias SQLite/PostgreSQL `1→0` e `2→0`; sete tabelas conceituais, FKs e índices; gate 50/50; `evidence/F1-FOUNDATION-SCHEMA.md` |
| Inbox/Outbox/ledger transacionais | em andamento | esquema materializado; application services e comportamento dual ainda pendentes |
| Motor durável e GNG-2 | pendente | — |
