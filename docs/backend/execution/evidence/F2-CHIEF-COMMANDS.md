# Evidência F2-ORCH-1b — comandos do Chief

Data: 2026-07-18. Commit funcional publicado: `6a23a45` em `develop`.

Os endpoints `pause`, `resume`, `handoff` e `drain` operam pela nova autoridade transacional `IChiefOrchestratorStore`. Pause/resume mantêm projeto e Chief coerentes. Handoff exige motivo, recusa definição especialista, cria nova instância com lease de um minuto, eleva o fencing global do projeto de 1 para 2 e remove o lease do antigo Chief antes de atualizar `chiefAgentId`.

O drain lê todas as tarefas nas colunas ativas, devolve cada uma a `ready`, cancela attempts de negócio em execução e também encerra execuções/attempts do motor durável com transição, fencing incrementado e Outbox durável. Agentes working/waiting ficam idle. Tarefa, projeção operacional do attempt, execução, agentes, projeto, ledger e Outbox são confirmados na mesma transação SQLite.

O cenário HTTP percorreu pause→resume, tentativa inválida de handoff para Software Engineer, handoff válido, trabalho de negócio e execução durável ativos e drain. Confirmou eventos `agent.statusChanged` e `audit.eventAppended` no stream global, `task.stateChanged` no projeto, attempt `cancelled`, execução `Cancelled` e recuperação integral após restart. A migration `0019_attempt_operational_state` mantém o lifecycle actor–critic interno separado do estado operacional exato do frontend.

`tools/backend/verify.sh` passou com restore locked, format, build Release 0 warnings/0 errors e 136/136 testes (`Unit 87`, `Integration 25`, `Contract 11`, `Recovery 4`, `Architecture 6`, `Concurrency 3`). `frontend/**` e `docs/frontend/**` permaneceram sem alterações do backend.
