# Refinamento v3 — edição manual como versão imutável

Data: 2026-07-19.

O contrato FR-3 do frontend passou a chamar `POST /api/v1/documents/{id}/versions` com apenas
`{ body }`. A autoridade backend já possuía conteúdo imutável, autoria, supersession, OCC,
catálogo em filesystem, ledger e Outbox, mas expunha somente o comando genérico
`POST /document-versions` e aceitava append apenas durante elaboração.

## Entrega

- nova rota body-only deriva tenant, documento, autor `user` e `authorId` da sessão local;
- cada salvamento cria `currentVersion + 1`, mantém as versões anteriores e grava conteúdo por
  path confinado + SHA-256;
- edição manual é aceita em `inElaboration`, `inReview` e `awaitingApproval`, os três estados
  oferecidos pela UI; estados terminais/aprovado retornam 409;
- quando há aprovação pendente, ela é religada atomicamente à nova `document_version_id` e tem
  sua versão OCC incrementada; assim, o encadeamento “salvar e aprovar” aprova exatamente o
  conteúdo novo, nunca a versão anterior;
- a regra foi alinhada no agregado fortemente tipado e nos stores SQLite/PostgreSQL;
- o evento canônico existente `document.stateChanged` registra `change=versionAppended`, sem
  inventar um evento fora do catálogo.

## Evidência executada

O teste HTTP real criou v2 pelo comando legado, v3 em revisão e v4 durante aprovação pendente,
confirmou autoria humana, aprovou v4, rejeitou edição pós-aprovação e releu os quatro corpos após
restart. O behavior dual-provider cria uma aprovação pendente, salva nova versão, comprova o
rebind e resolve a aprovação nos dois bancos. Testes focados verdes: 8 de domínio, 2 de integração
SQLite, 2 de contrato/OpenAPI e o cenário PostgreSQL gerenciado; zero recurso Docker gerenciado
permaneceu após cleanup. O gate integral passou frontend 331/331, build Release sem avisos/erros
e backend 246/246; o SAST dedicado executou 30 regras sobre 316 arquivos C#, aproximadamente
99,6% das linhas parseadas e zero achado.
