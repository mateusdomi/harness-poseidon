# DECISIONS — Frontend Harness Poseidon

Registro de decisões de engenharia e suposições não bloqueadoras, conforme o prompt de missão v1.1.

## D-001 — Monorepo único
- **Decisão:** o frontend vive em `frontend/` dentro do repositório `harness-poseidon`, sem `git init` próprio.
- **Justificativa:** exigência explícita da seção 0 do prompt de missão.

## D-002 — Branches permanentes: apenas `main` e `develop`
- **Decisão:** todo o desenvolvimento ocorre em `develop`; `main` só recebe merge com autorização humana explícita. Nenhuma branch adicional será criada.
- **Justificativa:** convenção Git obrigatória da seção 0.

## D-003 — Nome de produto
- **Decisão:** `product.name` = "Poseidon" (mock inicial); "Harness" permanece como codinome técnico.
- **Justificativa:** seção 5 do prompt de missão.

## D-004 — Bootstrap do repositório vazio
- **Decisão:** o remote estava vazio; criado commit mínimo de bootstrap em `main` (README, .gitignore), `main` e `develop` publicadas, desenvolvimento iniciado em `develop`.
- **Justificativa:** procedimento obrigatório da seção 0 para remoto vazio.

## D-005 — CRUD genérico tipado + comandos de domínio (ApiClient)
- **Decisão:** o `ApiClient` expõe verbos genéricos (`list/get/create/update/remove`) tipados por um `ResourceMap` central (37 recursos, chave = rota plural kebab-case), mais comandos de domínio explícitos (`moveTask`, `resolveApproval`, `startChatTurn`, ...). Recursos imutáveis (tarefas, solicitações, demandas, instruções, versões) ficam fora do `UpdateInputMap` — a imutabilidade é garantida em tempo de compilação, não por convenção.
- **Justificativa:** contract-first sem boilerplate de 37 sub-APIs; trocar mock ↔ http não mexe em componentes.

## D-006 — Re-sync realtime: snapshot + delta com SequenceTracker
- **Decisão:** `sequence` crescente por stream; o consumidor usa `SequenceTracker` (dedupe + detecção de lacuna). Em lacuna ou reconexão, pede `realtime.getSnapshot(stream)` e reaplica — o tracker descarta duplicados. Evento com lacuna não é aplicado até o re-sync.
- **Justificativa:** envelope exigido pela missão; mecanismo único e testável para dedupe/lacuna (provado em `mock-realtime.test.ts`).

## D-007 — Fixtures determinísticas com PRNG próprio (mulberry32 + ULID determinístico)
- **Decisão:** seed fixa 42; ULIDs gerados por `DeterministicUlidGenerator` (relógio base monotônico + contador, entropia do PRNG). O app cria um gerador separado (seed+1000) para entidades novas em runtime, sem colidir com as fixtures.
- **Justificativa:** fixtures estáveis para testes de round-trip e reprodução de cenários sem dependência externa.

## D-008 — msw opcional em dev; app usa MockApiClient em memória
- **Decisão:** no modo `mock` o app consome o `MockApiClient` direto (sem rede). O worker msw (`VITE_MSW=on`) serve as mesmas fixtures via HTTP `/api/v1` apenas para inspeção de tráfego no navegador. Testes nunca passam por rede.
- **Justificativa:** mock em memória emite eventos realtime nas mutações (sistema vivo); msw por HTTP não teria como empurrar eventos para o `MockRealtimeClient` sem acoplamento extra.
