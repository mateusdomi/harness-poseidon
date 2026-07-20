# Incidente P1 — first-run da Release Candidate

Data: 2026-07-20. Classificação: P1. Estado inicial: GNG-3 reprovado, Release Candidate No-Go.

## Sintoma e reprodução

A candidata `d11df779c5fe07a8f531aceaaf81bdcb8b64940b` abriu o shell, a sidebar e os
skeletons, mas não materializou conteúdo, vazio, erro ou retry no Cockpit. A instalação e o data
dir originais foram preservados; uma cópia bruta com permissão restrita e outra sanitizada ficam
sob `.artifacts/incidents/GNG-3-P1-d11df77-20260720/`, fora do Git.

A mesma candidata foi instalada em outro diretório, com data dir vazio e contexto Chromium sem
cookies. O onboarding apareceu e criou o perfil com `201`. Ao redirecionar para `/cockpit`, a tela
permaneceu com uma região de loading e quatro skeletons. Após cinco segundos:

- nenhuma request HTTP estava pendente;
- não havia erro de console;
- `profiles/current`, `projects`, `notifications`, `audit-events` e `budgets` tinham respondido
  `200`, com latência observada de até aproximadamente 11 ms;
- o cookie `harness.profile` era HttpOnly, SameSite Strict e estava presente;
- a captura sanitizada `02-cockpit-no-project.png` comprova visualmente o estado infinito.

O log original registra `SQLite persistence is ready; 46 migration(s) applied` antes de
`Now listening`, `Application started` e do primeiro `GET /`. Não houve erro de migration,
exception, deadlock, `401` ou `403` no fluxo que congelou a tela. As únicas requests duradouras
eram conexões WebSocket de `/hubs/events`; conexões anteriores encerraram corretamente com `101`.

## Causa técnica

A causa imediata é frontend: no estado válido “perfil criado, nenhum projeto selecionado”, queries
desabilitadas por ausência de `projectId` continuam com `isPending=true`; o cálculo agregado de
loading do Cockpit tratava esse `isPending` como request ativa. Por isso o skeleton não terminava,
apesar de todas as APIs terem respondido. O E2E Host real anterior criava organização e projeto
antes de validar o Cockpit e mascarava esse estado intermediário crítico.

A investigação encontrou também um defeito independente de sessão pessoal no backend: cookie com
ULID sintaticamente válido, porém inexistente no banco, fazia `profiles/current` responder `404` e
APIs protegidas responderem `401`. A recuperação automática só cobria cookie ausente ou malformado.
O middleware foi endurecido para validar a existência e substituir uma sessão stale pelo perfil
pessoal persistido.

O favicon da candidata original respondeu `404`; ele permanece critério bloqueante do smoke de
assets do pacote.

## Barreiras de recorrência

- Launcher grava o arquivo de runtime e permite abrir o navegador somente depois de verificar
  shell, health, perfil/onboarding, APIs essenciais, snapshot realtime, negociação SignalR e IPC
  autenticado do Runner, com timeout e último diagnóstico seguro.
- `status` e `doctor` exercitam APIs reais; `/health` isolado não promove estado operacional.
- O gate da RC instala em diretório externo, começa sem demo e com data dir vazio, usa navegador
  descartável diretamente contra o pacote, interrompe após criar somente o perfil para exigir
  Cockpit sem skeleton, valida cookie stale, cria organização/projeto, reinicia e comprova
  persistência.
- O pacote só é aceito quando seu manifesto registra o SHA limpo, sem sufixo `-dirty`.

## Evidência executada nesta investigação

- reprodução Chromium limpa: falhou de forma determinística no timeout do novo gate enquanto a UI
  defeituosa estava integrada, confirmando que a barreira captura a regressão;
- testes focados de sessão e Launcher: 4/4 verdes;
- build `Release`: zero warnings e zero erros;
- probes manuais do novo `status` nos modos `onboarding` e `personal-session`: verdes.

A nova candidata só pode receber recomendação técnica Go após a correção frontend ser publicada em
`origin/develop`, os contratos/governança serem regenerados e o gate completo passar usando o
artefato self-contained.
