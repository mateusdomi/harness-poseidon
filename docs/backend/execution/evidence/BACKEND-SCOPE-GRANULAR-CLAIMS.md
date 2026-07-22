# Write-scope de backend + claims granulares (destrava o Piloto 2 completo)

Data: 2026-07-22. Branch: `develop`. Base: `c16e08d`.

## Contexto — dois gaps reais que impediam o cenário completo

Ao preparar o Piloto 2 do cenário final (chefe orquestrando backend + frontend + múltiplas
instâncias), a investigação mostrou dois limites REAIS do produto:

1. **Backend sem write-scope governado.** `agent-runs` recusava qualquer papel sem escopo de
   claim, e só `frontend-specialist` tinha (`PathScopesFor(backend)` era vazio) → um worker
   `backend-specialist` era rejeitado com `role_has_no_path_scope`.
2. **Concorrência de instâncias limitada pelo escopo fixo do papel.** O claim vinha fixo do
   papel (`frontend/**` inteiro), então duas instâncias frontend colidiam — impossível rodar
   N instâncias em paralelo.

O `ScopeClaimRegistry` **já** é granular (usa `ScopeClaim.Intersects`: sub-paths disjuntos
coexistem) e o `AgentPathScopePolicy` **já** sabe validar claims backend (tem `BackendRoots`,
`SharedRoots`, `SharedFiles`). Faltava só ligar as pontas.

## Mudança (cirúrgica, sem quebrar a invariante)

- `AgentRoles.PathScopesFor(backend-specialist)` passou a devolver um escopo PADRÃO dentro do
  limite backend (`src/**`, `tests/**`, `docs/backend/**`, `docs/contracts/**`,
  `docs/architecture/**`, `docs/decisions/**`, `infra/**`, `tools/backend/**`, `governance/**`)
  — nunca `frontend/**`.
- `StartAgentRunApiRequest` ganhou `scopeClaims` opcional. O endpoint usa os claims do pedido
  quando presentes (para ESTREITAR a sub-paths — concorrência granular), senão o padrão do
  papel. O **limite** continua do papel: `AgentPathScopePolicy.Evaluate` (no endpoint, cedo, e
  no orquestrador) recusa qualquer claim fora do papel — um cliente ESTREITA, nunca AMPLIA.
  O bypass que CA-1 fechou (escolher o próprio papel/escopo) continua fechado.

## Efeito

- Backend passa a executar via `agent-runs` de forma governada (não mais `role_has_no_path_scope`).
- Múltiplas instâncias (mesma conta ou não) rodam concorrentes quando reivindicam subárvores
  DISJUNTAS; o mesmo escopo continua colidindo por design.

## Testes

- `AgentAccountConfigurationTests.TheBackendRoleNowOwnsAWriteScopeEntirelyWithinItsBoundary` —
  backend tem escopo não-vazio, todo aprovado por `AgentPathScopePolicy(Backend)` e sem nenhum
  path de frontend; frontend inalterado; papel desconhecido segue vazio.
- `ScopeClaimRegistryTests.MultipleInstancesRunConcurrentlyOnDisjointSubtreesButCollideOnOverlap`
  — 3 instâncias em subárvores disjuntas adquirem juntas; a 4ª sobreposta é bloqueada.
- `AgentRunContractDriftTests.TheStartContractAcceptsScopeClaimsThatOnlyNarrowWithinTheRoleBoundary`
  — o schema publica `scopeClaims`; o limite é imposto pelo servidor.
- OpenAPI publicado regenerado (`docs/contracts/openapi.json`).

Regressão verde: UnitTests 302/302, ContractTests 41/41, ArchitectureTests 7/7, format limpo,
build Release 0 warnings, secrets/governança verdes.

## Próximo

Com backend + claims granulares destravados, o Piloto 2 do cenário completo (chefe
orquestrando backend + frontend + N instâncias em subárvores disjuntas, critic Antigravity
live, restart/recovery) pode ser executado de verdade.
