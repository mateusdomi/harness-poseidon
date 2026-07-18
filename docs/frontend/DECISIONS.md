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

## D-009 — Evolução dos contratos para FE-1a (marca, criticidade, repositório, settings)
- **Decisão:** os contratos de `organizations`, `projects` e `settings` foram estendidos no próprio `src/api/contracts` (campos novos obrigatórios com defaults no mock/fixtures): `brand` (logo/cores/tipografia, `null` = herda), `defaultWorkflowTemplateIds`, `templateKeys`, `policies` na organização; `state`, `criticality` (reusa o enum `priority`), `repositoryProvider`, `defaultBranch`, `technologies`, `brand`, `memberProfileIds`, `configVersion`, `lastActivityAt` no projeto; `workingDirectory` e `unsafeModeAcceptedAt` no settings. `profiles` e `organizations` entraram no `CreateInputMap`; `organizations` no `UpdateInputMap`; `projects.update` ganhou os campos novos. O mock incrementa `configVersion` quando campos versionados (repositório, tecnologias, marca) mudam.
- **Justificativa:** a camada de API era o contrato "completo" da FE-0, mas o domínio desta fatia (marca herdável, abas de projeto, aceite de modo inseguro) não existia nela. Estender schema + mock + fixtures mantém contract-first e round-trip verde (89 testes de API inalterados e passando).

## D-010 — Gate de onboarding via sessão local (zustand persistida) + rota fora do shell
- **Decisão:** `src/stores/session-store.ts` (`poseidon-session` no localStorage) guarda `activeProfileId`. O componente `RequireProfile` envolve a rota do AppShell no router: sem perfil ativo, tudo redireciona para `/onboarding` (com `state.from` para voltar). `/onboarding` é rota standalone (tela cheia, sem AppShell) e fora do guard. A página decide: 0 perfis → wizard; ≥1 perfil → seleção de perfil (criar novo abre o wizard).
- **Justificativa:** modo pessoal com perfis locais exige um gate simples e testável; manter o onboarding fora do shell evita navegação chrome em primeiro uso.

## D-011 — Aceite do modo inseguro persistido em `settings.unsafeModeAcceptedAt`
- **Decisão:** no mock o sandbox é considerado indisponível; o wizard exige checkbox explícito (`z.literal(true)`) e persiste o timestamp via `update('settings', …)`. O mock cria settings padrão junto com todo perfil novo (`#build('profiles')`), e o wizard atualiza tema/idioma/diretório/aceite em seguida. Exibição permanente do aceite na tela de Configurações fica para a fatia da feature settings (FE-1b/c).
- **Justificativa:** o aceite é dado de perfil, não de sessão — settings é o recurso por-perfil já existente.

## D-012 — Resolver zod próprio em vez de `@hookform/resolvers`
- **Decisão:** `src/lib/form.ts` implementa um resolver minimalista (safeParse → erros aninhados por path). Mensagens de validação são CHAVES i18n (ex.: `common.validation.required`), traduzidas na renderização.
- **Justificativa:** `@hookform/resolvers` não era dependência do projeto; o resolver próprio tem ~40 linhas, sem nova dependência, e padroniza mensagens como chaves de catálogo.

## D-013 — Navegação intra-feature por estado de view (sem sub-rotas nesta fatia)
- **Decisão:** detalhe/criação/edição de organizações e projetos são estados de view dentro da página (`list | detail | create | edit`), sem novas rotas. Busca e filtros (organização, criticidade, estado) são client-side sobre a lista completa. Listas atualizam por invalidação do React Query após mutations (o catálogo de eventos realtime não tem eventos de projeto/organização/perfil — "evento/invalidação": aqui se aplica invalidação).
- **Justificativa:** simplicidade mobile-first e escopo da fatia; deep-linking (`/projects/:id`) e filtros server-side entram quando o backend real existir.
