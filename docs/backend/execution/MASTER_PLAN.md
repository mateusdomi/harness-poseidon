# Plano mestre

Fonte de verdade: missão v1.3 fornecida pelo usuário. Este arquivo acompanha execução; não redefine requisitos.

## Caminho crítico e gates

| Ordem | Fase | Marco | Estado |
|---|---|---|---|
| 1 | F0 — Bootstrap e nove PoCs | GNG-1 | concluída; gate verde |
| 2 | F1 — Fundação determinística | GNG-2 | concluída; gate verde |
| 3 | F2 — MVP pessoal e integração frontend | GNG-3 | próximo caminho crítico |
| 4 | F3 — Workflows completos/autônomo | gate da fase | pendente |
| 5 | F6, F4, F5 — execução, PO Assistant, prototipação | gates das fases | pendente |
| 6 | F7 e F8 — desktop e licenciamento | GNG-4 | pendente |
| 7 | F9 — canais externos | gate da fase | pendente |
| 8 | F10 — servidor multiusuário | GNG-5 | pendente |
| 9 | F11 — hardening e release | GNG-6 | pendente |

## Fase 0 — fatias retomáveis

1. EP-01: documentação viva, ambiente, SDK local, solução e pipeline.
2. EP-02: SharedKernel e contratos primitivos.
3. Testes de arquitetura e ADR-001 a ADR-014.
4. PoC-1: SQLite WAL + dispatcher único — verde em 2026-07-18.
5. PoC-2: kill abrupto + reconciliação — verde em 2026-07-18.
6. PoC-3: lease + fencing — verde em 2026-07-18.
7. PoC-4: Codex CLI subprocesso/checkpoint/reidratação — verde em 2026-07-18.
8. PoC-5: Git fixtures, branches/worktrees e claims — verde em 2026-07-18.
9. PoC-6: sandbox Docker com limites, proxy/egress e cleanup — verde em 2026-07-18.
10. PoC-7: SignalR com sequência e re-sync — verde em 2026-07-18.
11. PoC-8: PostgreSQL gerenciado e `SKIP LOCKED` — verde em 2026-07-18.
12. PoC-9: IPC loopback autenticado e idempotente — verde em 2026-07-18.
13. Suíte completa e GNG-1 — verdes em 2026-07-18, 49/49 testes.

## Fase 1 — fatias retomáveis

1. EP-03: esquema relacional de produção e migrations dual-provider — verde em 2026-07-18.
2. EP-04: processamento transacional de Inbox/Outbox e primeiro elo do ledger — verde em 2026-07-18.
3. EP-04/EP-07: mover IPC Host–Runner para a autoridade relacional e persistir sequências — verde em 2026-07-18.
4. EP-05a: contrato completo, máquina de estados e retry determinístico — verde em 2026-07-18.
5. EP-05b: schema dual-provider para execução, tentativa, checkpoint, timer, sinal, transição, dead-letter, Inbox e Outbox — verde em 2026-07-18.
6. EP-05c.1: engine SQLite completo para leases, heartbeats, fencing, checkpoints, timers, retry e dead-letter — verde em 2026-07-18.
7. EP-05c.2: engine PostgreSQL com o mesmo comportamento e aquisição `SKIP LOCKED` — verde em 2026-07-18.
8. EP-05d: critério de recuperação GNG-2 com encerramento abrupto e auditoria completa nos dois providers — verde em 2026-07-18.
9. EP-06/EP-09a: contrato e agregado provider-neutral da cadeia Solicitação→Revisão — verde em 2026-07-18.
10. EP-09b.1: migrations dual-provider da cadeia — verde em 2026-07-18.
11. EP-09b.2a: criação/snapshot transacional dual-provider com Inbox, ledger e Outbox — verde em 2026-07-18.
12. EP-09b.2b.1: mutações de attempt/evidência/review com concorrência otimista e actor–critic — verde em 2026-07-18.
13. EP-09b.2b.2: correção por nova instrução imutável e reidratação completa — verde em 2026-07-18.
14. EP-10a: contrato provider-neutral de WorkflowDefinition/Run, fases, gates e progresso objetivo — verde em 2026-07-18.
15. EP-10b.1: schema dual-provider de definições e runs — verde em 2026-07-18.
16. EP-10b.2a: criação/publicação transacional de definição — verde em 2026-07-18.
17. EP-10b.2b.1: inicialização transacional de run e projeções objetivas — verde em 2026-07-18.
18. EP-10b.2b.2: start/pause/resume/cancel transacionais com optimistic concurrency — verde em 2026-07-18.
19. EP-10b.2b.3: objetivos/gates/fases, reidratação e progresso dual-provider — verde em 2026-07-18.
20. F1-DOC-1a: agregado de documentos, versões imutáveis, aprovações e órfãos — verde em 2026-07-18.
21. F1-DOC-1b: schema dual-provider append-only de documentos — verde em 2026-07-18.
22. F1-DOC-1c.1: criação/leitura transacional e idempotente dual-provider — verde em 2026-07-18.
23. F1-DOC-1c.2: append de versão com OCC, supersession e auditoria dual-provider — verde em 2026-07-18.
24. F1-DOC-1c.3: classificação/fase e lifecycle com OCC/histórico — verde em 2026-07-18.
25. F1-DOC-1c.4: aprovações documentais com pendência única e eventos — verde em 2026-07-18.
26. F1-WRK-1a: contrato/schema de claim, fencing, retry e dead-letter da Outbox — verde em 2026-07-18.
27. F1-WRK-1b: stores transacionais dual-provider com fencing/retry/dead-letter — verde em 2026-07-18.
28. F1-WRK-1c: BackgroundService de dispatch, cancellation e restart — verde em 2026-07-18.
29. F1-WRK-1d.1: contrato/schema append-only do stream realtime — verde em 2026-07-18.
30. F1-WRK-1d.2: stores sequenciados dual-provider — verde em 2026-07-18.
31. F1-WRK-1d.3a: sink persistido da Outbox e replay sem retransmissão — verde em 2026-07-18.
32. F1-WRK-1d.3b: wiring do Host, snapshot SignalR persistido e resync após restart — verde em 2026-07-18.
33. F1-WRK-2: watchdog/reconciliador dual-provider — verde em 2026-07-18.
34. Suíte completa, recuperação abrupta automática e encerramento formal da Fase 1/GNG-2 — verde em 2026-07-18.

## Fase 2 — ordem inicial retomável

1. Sincronizar `origin/develop`, reler estado/contratos atuais do frontend e registrar drift sem editar sua área — verde em 2026-07-18.
2. Fatia vertical de perfil local: domínio, aplicação, persistência, API, eventos aplicáveis, testes e OpenAPI — verde em 2026-07-18.
3. Organizações — verde em 2026-07-18.
4. Projetos — verde em 2026-07-18.
5. Cockpit/digest — verde em 2026-07-18.
6. Conversas e chat streaming — verde em 2026-07-18.
7. Solicitações, demandas, tarefas, tentativas e quadro — verde em 2026-07-18.
8. Workflows, gates e progresso — verde em 2026-07-18.
9. Documentos e aprovações — verde em 2026-07-18.
10. Orquestrador e agentes — catálogo/instâncias verde; comandos do Chief em execução.
11. Ferramentas, skills, plugins e MCP — pendente.
12. Notificações — pendente.
13. Governança e auditoria — pendente.
14. Complementares e integração frontend/dogfood — pendente.

## Backlog épico

EP-01 Bootstrap/pipeline; EP-02 SharedKernel/contratos; EP-03 Persistência dual; EP-04 Ledger/Outbox/Inbox; EP-05 Motor durável; EP-06 Identity/Organizations/Projects; EP-07 Conversations/gateway; EP-08 Chefe; EP-09 cadeia Solicitação→Tentativa; EP-10 Workflows/gates/progresso; EP-11 Runner/Sandbox/Codex executor; EP-12 Git; EP-13 API/SignalR/OpenAPI; EP-14 integração frontend; EP-15 rodar projeto; EP-16 PO Assistant; EP-17 Prototipação; EP-18 Empacotamento; EP-19 Licenciamento; EP-20 Canais; EP-21 Servidor; EP-22 Hardening/release.

## Regra de avanço

Cada fatia: implementar → build/test/lint → registrar evidência → atualizar estado → commit → `fetch` + `rebase origin/develop` → repetir testes afetados → push normal. Nenhum gate é contornado e nenhum merge em `main` é autorizado.
