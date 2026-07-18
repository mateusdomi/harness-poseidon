# Evidência F2-APP-1 — central unificada de aprovações

Data: 2026-07-18. Commit funcional publicado: `dedb29a` em `develop`.

A fila `approvals` une as solicitações documentais da autoridade F1 com `general_approval_requests` para tarefa, gate e decisão humana. Todos expõem o contrato TypeScript único, inclusive vínculos nullable, prioridade, prazo, solicitante, decisão e nota. A migration SQLite `0017_general_approvals` é aditiva e a execução fresca/repetida ficou `17→0`.

O cenário HTTP criou e resolveu duas aprovações documentais, uma de tarefa e uma decisão humana, comprovou fila consolidada, eventos `approval.requested/resolved` no stream do projeto e recuperação após restart. O cenário de workflow criou aprovação de gate e tentou resolvê-la antes dos pré-requisitos: recebeu 409 e a transação foi revertida. Após fase ativa e objetivos mínimos, a mesma approval foi resolvida, atualizando gate+objetivo e publicando `gate.changed` atomicamente com `approval.resolved`.

`tools/backend/verify.sh` passou com restore locked, format, build Release 0 warnings/0 errors e 134/134 testes (`Unit 87`, `Integration 24`, `Contract 10`, `Recovery 4`, `Architecture 6`, `Concurrency 3`). `frontend/**` e `docs/frontend/**` permaneceram sem alterações do backend.
