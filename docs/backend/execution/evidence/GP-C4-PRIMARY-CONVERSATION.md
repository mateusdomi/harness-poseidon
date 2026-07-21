# GP-C4 — primeira conversa idempotente

Data: 2026-07-21. Branch: `develop`.

Quarta parte da Fatia C (ADR-019). A jornada inicial deixa de exigir que o usuário descubra
"Nova conversa" para desbloquear o input do chat.

## Entrega

- `POST /api/v1/projects/{projectId}/conversations/primary` — comando **idempotente**:
  devolve a conversa ativa mais antiga do projeto (`200`) ou cria a primeira (`201`).
  Reexecutar não duplica nem altera histórico; corrida concorrente cai na leitura
  idempotente em vez de conflitar.
- **Conversa é separada da execução.** Criar conversa não é executar: continua permitido
  mesmo com a execução bloqueada por prontidão. Isso preserva a jornada — o usuário escreve,
  a mensagem é persistida e o bloqueio é explicado (C2).
- **Envio bloqueado preserva a conversa**: ela permanece `active` e única após o turno
  bloqueado; a mensagem humana fica no histórico.
- Autoria e timestamps seguem o contrato existente (`createdByProfileId`, `createdAt`,
  `lastMessageAt` em UTC ISO-8601); nenhuma resposta fictícia é produzida em nenhum ponto.

## Prova

`EmptyInstallFailClosedTests` foi estendido: numa instalação vazia sem provider/modelo, o
comando cria a conversa (`201`), a reexecução devolve a mesma conversa (`200`, mesmo `id`),
a listagem do projeto continua com exatamente uma conversa, e após o turno bloqueado a
conversa permanece `active` e única com a mensagem humana persistida.

## Gates executados

- `dotnet build Harness.sln -c Release`: 0 avisos, 0 erros.
- `dotnet format --verify-no-changes`: limpo.
- Suíte integral: **319** testes verdes — 177 unit, 95 integration, 31 contract,
  7 architecture, 6 recovery, 3 concurrency.
- `tools/backend/export-contracts.sh`: OpenAPI republicado com a nova rota; drift assere-a.
- Governança: `sync` até `changed=0`, `generate`, `lint --warnings-as-errors` verde.
- `tools/backend/scan-secrets.sh`: limpo.

Nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi modificado.

## Não incluído

Criação implícita no primeiro envio (criar conversa automaticamente dentro do `POST /turns`)
não foi adotada: manteria a ambiguidade entre "escrever" e "executar" e criaria conversa como
efeito colateral de uma execução que pode estar bloqueada. O comando explícito e idempotente
cobre o objetivo de produto — a UI pode chamá-lo ao abrir o chat sem interação do usuário.

Resta **C5**: validação ponta a ponta da execução real com credencial externa
(`SKIPPED_EXTERNAL_CREDENTIALS` quando ausente).
