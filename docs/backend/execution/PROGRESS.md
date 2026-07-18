# Progresso e evidências

## Estado dos gates

| Gate | Critério resumido | Estado | Evidência |
|---|---|---|---|
| GNG-1 | nove PoCs verdes | verde | PoCs 1–9 executadas e comprovadas; pipeline 49/49 |
| GNG-2 | recuperação abrupta com auditoria completa | critério técnico verde; fase aberta | `SIGKILL` dual-provider, 6/6 checkpoints, 2 attempts e cadeia de 7 eventos íntegra; restante do escopo F1 pendente |
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
| Esquema relacional dual-provider | verde | migrations atuais SQLite/PostgreSQL `2→0` e `3→0`; fundação e IPC com FKs/índices provider-specific; `evidence/F1-FOUNDATION-SCHEMA.md` e `evidence/F1-RUNNER-IPC-PERSISTENCE.md` |
| Inbox/Outbox/ledger transacionais | verde | 10 comandos concorrentes = 1 aplicação/9 replays; conflito e rollback sem efeitos; ledger/outbox/inbox atômicos nos dois providers; `evidence/F1-TRANSACTIONAL-FOUNDATION.md` |
| IPC sobre autoridade relacional | verde | comportamento comum dual-provider, Runner real, Host reiniciado, replay 3/3 sem duplicar Inbox/Outbox; gate 52/52; `evidence/F1-RUNNER-IPC-PERSISTENCE.md` |
| Contrato do motor durável | verde | interface completa, matriz exaustiva de transições e backoff determinístico/capado; gate 58/58; `evidence/F1-DURABLE-ENGINE-CONTRACT.md` |
| Schema do motor durável | verde | nove tabelas em migrations próprias `SQLite 3→0` / `PostgreSQL 4→0`, constraints de provider validadas; `evidence/F1-DURABLE-ENGINE-SCHEMA.md` |
| Borda comum do motor | verde | codecs exaustivos, validação canônica ULID/JSON/bounds/lease e fingerprint SHA-256; gate 61/61; `evidence/F1-DURABLE-ENGINE-BOUNDARY.md` |
| Motor durável SQLite | verde | contrato completo executado: lifecycle, Inbox, aquisição concorrente, fencing, checkpoint, retry/dead-letter, timer/sinal e reconciliação; gate 62/62; `evidence/F1-DURABLE-ENGINE-SQLITE.md` |
| Motor durável PostgreSQL | verde | mesmo comportamento completo do SQLite com locks transacionais e aquisição `FOR UPDATE SKIP LOCKED`; gate 62/62; `evidence/F1-DURABLE-ENGINE-POSTGRES.md` |
| Recuperação abrupta GNG-2 | verde | `SIGKILL` real após 3/6 checkpoints em SQLite e PostgreSQL; retomada 6/6, fencing crescente, 8 Inbox, 6 transições/Outbox e 7 elos válidos; `evidence/F1-GNG2-RECOVERY.md` |
| Contrato/agregado EP-09a | verde | cadeia de negócio append-only, evidência, tentativa única, correção e actor–critic por risco; 8/8 cenários; `evidence/F1-WORK-CHAIN-CONTRACT.md` |
| Schema EP-09b.1 | verde | 7 tabelas provider-specific, FKs compostas, checks e tentativa ativa única; migrations SQLite `4→0` / PostgreSQL `5→0`; `evidence/F1-WORK-CHAIN-SCHEMA.md` |
| Store EP-09b.2a | verde | criação/snapshot dual-provider: 10 concorrentes = 1 aplicação/9 replays; estado+Inbox+ledger+Outbox atômicos; `evidence/F1-WORK-CHAIN-STORE.md` |
| Store EP-09b.2b.1 | verde | start/complete/review dual-provider com optimistic concurrency, Inbox, ledger e Outbox; actor–critic médio; snapshot v4/1 attempt/1 evidência/1 review; `evidence/F1-WORK-CHAIN-MUTATIONS.md` |
| Store EP-09b.2b.2 | verde | correção imutável v2→v1, segunda tentativa aprovada e leitura transacional integral; final v8/2 instruções/2 attempts/2 evidências/2 reviews; `evidence/F1-WORK-CHAIN-HISTORY.md` |
| Contrato EP-10a | verde | 9 cenários: versão/publicação, lifecycle, fases, gates, pausa e progresso ponderado recomputável até 100/100/100; `evidence/F1-WORKFLOW-CONTRACT.md` |
| Schema EP-10b.1 | verde | 10 tabelas, FKs/checks/índice de fase ativa, migrations idempotentes SQLite `5→0` e PostgreSQL `6→0`; `evidence/F1-WORKFLOW-SCHEMA.md` |
| Store EP-10b.2a | verde | definição inicial publicada atomicamente: 10 concorrentes=1 aplicação/9 replays, snapshot hierárquico e Inbox/ledger/Outbox dual-provider; `evidence/F1-WORKFLOW-STORE.md` |
| Store EP-10b.2b.1 | verde | run pendente inicializado a partir de versão publicada com projeções e Inbox/ledger/Outbox atômicos; 10 concorrentes=1 aplicação/9 replays; snapshot 1/2/1 e progresso 0/0/0; `evidence/F1-WORKFLOW-RUN-CREATION.md` |
| Store EP-10b.2b.2 | verde | start/pause/resume/cancel dual-provider com versão esperada, fase ativa única, rejeições idempotentes e auditoria somente para aplicações; `evidence/F1-WORKFLOW-RUN-LIFECYCLE.md` |
| Store EP-10b.2b.3 | verde | 2 fases até run v14/100-100-100; avanço monotônico, gate failed→passed, bloqueio, ativação ordenada, snapshot hierárquico e 10 concorrentes=1 aplicação/9 replays; `evidence/F1-WORKFLOW-RUN-PROGRESS.md` |
| F1-DOC-1a | verde | agregado cataloga conteúdo por path+SHA-256, versão/supersession imutável, órfão/classificação, lifecycle e aprovação/rejeição/cancelamento; 8/8 cenários; `evidence/F1-DOCUMENT-CONTRACT.md` |
| F1-DOC-1b | verde | 5 tabelas, FKs compostas, aprovação pendente única, índice de órfãos e triggers append-only; migrations SQLite `6→0` / PostgreSQL `7→0`; `evidence/F1-DOCUMENT-SCHEMA.md` |
| F1-DOC-1c.1 | verde | criação/leitura dual-provider: 10 concorrentes=1 aplicação/9 replays; catálogo+Inbox+ledger+Outbox atômicos e snapshot integral; `evidence/F1-DOCUMENT-STORE-CREATION.md` |
| F1-DOC-1c.2 | verde | append v2→v1 com OCC e `FOR UPDATE`/dispatcher; 10 concorrentes=1 aplicação/9 replays; stale/ausente sem auditoria falsa; `evidence/F1-DOCUMENT-VERSIONING.md` |
| F1-DOC-1c.3 | verde | órfão adotado por fase sem histórico falso; lifecycle com matriz fechada, ator/nota, OCC e transição append-only; 10 concorrentes=1/9; `evidence/F1-DOCUMENT-LIFECYCLE.md` |
| F1-DOC-1c.4 | verde | request/cancel/reject/correct/reapprove dual-provider; pendência única, nota obrigatória, v12/3 versões/3 approvals/8 transições; `evidence/F1-DOCUMENT-APPROVALS.md` |
| F1-WRK-1a | verde | contrato de claim/fencing/retry/dead-letter; migrations SQLite `7→0`/PostgreSQL `8→0`, histórico append-only e backoff 2/2; `evidence/F1-OUTBOX-DISPATCH-SCHEMA.md` |
| F1-WRK-1b | verde | 10 workers→2 claims únicos; expiry/fencing 1→2, retry token 3, stale recusado, 1 dispatch/1 dead-letter/2 falhas nos dois providers; `evidence/F1-OUTBOX-STORES.md` |
| F1-WRK-1c | próximo | `OutboxDispatcherBackgroundService` com sink fake, retry, cancellation e restart |
