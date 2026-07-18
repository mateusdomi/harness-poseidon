# Plano mestre

Fonte de verdade: missão v1.3 fornecida pelo usuário. Este arquivo acompanha execução; não redefine requisitos.

## Caminho crítico e gates

| Ordem | Fase | Marco | Estado |
|---|---|---|---|
| 1 | F0 — Bootstrap e nove PoCs | GNG-1 | concluída; gate verde |
| 2 | F1 — Fundação determinística | GNG-2 | em andamento |
| 3 | F2 — MVP pessoal e integração frontend | GNG-3 | pendente |
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
11. EP-09b.2: store transacional/reidratação dual-provider com Inbox, ledger e Outbox — próximo incremento.
12. EP-10: WorkflowDefinition/Run, fases, gates e progresso objetivo.
13. Documentos/versionamento/aprovações, workers e SignalR persistido; então fechar formalmente a Fase 1/GNG-2.

## Backlog épico

EP-01 Bootstrap/pipeline; EP-02 SharedKernel/contratos; EP-03 Persistência dual; EP-04 Ledger/Outbox/Inbox; EP-05 Motor durável; EP-06 Identity/Organizations/Projects; EP-07 Conversations/gateway; EP-08 Chefe; EP-09 cadeia Solicitação→Tentativa; EP-10 Workflows/gates/progresso; EP-11 Runner/Sandbox/Codex executor; EP-12 Git; EP-13 API/SignalR/OpenAPI; EP-14 integração frontend; EP-15 rodar projeto; EP-16 PO Assistant; EP-17 Prototipação; EP-18 Empacotamento; EP-19 Licenciamento; EP-20 Canais; EP-21 Servidor; EP-22 Hardening/release.

## Regra de avanço

Cada fatia: implementar → build/test/lint → registrar evidência → atualizar estado → commit → `fetch` + `rebase origin/develop` → repetir testes afetados → push normal. Nenhum gate é contornado e nenhum merge em `main` é autorizado.
