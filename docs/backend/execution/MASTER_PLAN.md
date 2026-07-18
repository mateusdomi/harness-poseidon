# Plano mestre

Fonte de verdade: missão v1.3 fornecida pelo usuário. Este arquivo acompanha execução; não redefine requisitos.

## Caminho crítico e gates

| Ordem | Fase | Marco | Estado |
|---|---|---|---|
| 1 | F0 — Bootstrap e nove PoCs | GNG-1 | em andamento |
| 2 | F1 — Fundação determinística | GNG-2 | pendente |
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
11. PoC-8: PostgreSQL gerenciado e `SKIP LOCKED` — próximo incremento.
12. PoC-9: IPC loopback autenticado e idempotente.
13. Executar suíte completa, catalogar evidências e avaliar GNG-1.

## Backlog épico

EP-01 Bootstrap/pipeline; EP-02 SharedKernel/contratos; EP-03 Persistência dual; EP-04 Ledger/Outbox/Inbox; EP-05 Motor durável; EP-06 Identity/Organizations/Projects; EP-07 Conversations/gateway; EP-08 Chefe; EP-09 cadeia Solicitação→Tentativa; EP-10 Workflows/gates/progresso; EP-11 Runner/Sandbox/Codex executor; EP-12 Git; EP-13 API/SignalR/OpenAPI; EP-14 integração frontend; EP-15 rodar projeto; EP-16 PO Assistant; EP-17 Prototipação; EP-18 Empacotamento; EP-19 Licenciamento; EP-20 Canais; EP-21 Servidor; EP-22 Hardening/release.

## Regra de avanço

Cada fatia: implementar → build/test/lint → registrar evidência → atualizar estado → commit → `fetch` + `rebase origin/develop` → repetir testes afetados → push normal. Nenhum gate é contornado e nenhum merge em `main` é autorizado.
