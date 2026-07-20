# Evidência — Governance Gate P1

Data: 2026-07-20. Owner: Platform Governance. Branch: `develop`.

## G4 — bundles e receipts

- Todo turno adquirido pelo `ChiefTurnBackgroundService` monta o bundle e persiste receipt antes de
  chamar o actor. Falha de manifest/documento produz fallback bloqueador e receipt `0.0.0` em vez de
  execução sem governança.
- O teste dogfood percorreu Host real, SQLite, Chief Fake, bundle, receipt, evaluator independente e
  completion: estado `completed`, documentos não vazios, gate `pass` e métrica de evaluator.
- O checksum reproduz o contexto entregue; cache é tenant/project scoped. Núcleo, critérios de
  aceite, stop conditions e budget permanecem quando o restante é truncado.
- O mesmo behavior de persistência passou em SQLite e PostgreSQL. O teste de recovery fechou o
  primeiro dispatcher após `selected`, reabriu o banco, reaplicou migrations como no-op e concluiu
  via OCC na versão 2.

## G5–G7 — gates mecânicos

- O evaluator 1.0.0 recebe critérios, diff, evidências e resultados de teste sem histórico do actor
  nem ferramentas de escrita. Risco medium/high/critical exige identidade distinta; ausência de
  evidência, teste ou critério falha por padrão.
- O benchmark automatizado executou 100 fixtures com edição válida e conteúdo concorrente. Ambas
  estratégias acertaram 100 edições frescas. A edição anterior teve 0 stale rejections, 100 retries,
  104400 tokens estimados e 100 regressões; hashline teve 100 stale rejections, 0 retries, 55780
  tokens estimados e 0 regressões. Tempo medido é publicado em microssegundos pelo contrato, sem
  limiar dependente de hardware. Hashline permanece padrão com flag de rollback.
- O teste de filesystem comprovou rejeição sem escrita, aplicação com hash correto, troca atômica,
  ausência de temporário residual e dois audit records. A API exige receipt tenant/project-scoped,
  controlled root, policy de agente e claim persistido para paths compartilhados.
- O stale-doc detector emite findings/tarefas, não remove nem altera fonte. Checksum do documento
  antes/depois permaneceu idêntico no teste.

## G8 — executor OMP RPC

- Catálogo com quatro executores; OMP ausente resulta `available=false/binary_not_found` e não impede
  startup. Ativação exige configuração explícita; o produto não depende do binário.
- Fake RPC determinístico comprovou execute, heartbeat, chunk, result, schema, cancelamento
  cooperativo e cleanup. Benchmark de confiabilidade: 20/20 turnos concluídos, custo de provider
  zero e nenhuma rede/modelo real. O smoke real não foi executado porque
  `HARNESS_RUN_REAL_AGENT_TESTS` permaneceu desligado, como exige o gate de CI.
- Sandbox exige rootfs read-only, worktree isolada, egress restrito e resource limits. ADR-016
  registra decisão, protocolo próprio, upstream analisado e atribuição MIT; nenhum código OMP foi
  copiado.

## Flags e rollback

- `Harness:Governance:Features:ContextBundlesEnabled`
- `Harness:Governance:Features:HashlinePatchesEnabled`
- `Harness:Governance:Features:StaleDocumentDetectorEnabled`
- `Harness:Governance:Evaluator:Enabled`, provider, model e risk tiers
- `Harness:AgentExecutors:OmpRpc:Enabled`
- `Harness:IsolatedExecution:ExecutorId=omp-rpc`

Receipts permanecem obrigatórios durante rollback. Contratos estão em `docs/contracts/openapi.json`;
migrations continuam separadas por provider.

## Gate integral

| Verificação | Resultado |
|---|---|
| `tools/backend/verify.sh` | exit 0 |
| Linter documental | `errors=0`, `warnings=0` |
| Build .NET Release | 0 erros, 0 warnings |
| Frontend protegido | 49 arquivos, 413/413 testes, audit de produção 0 |
| Backend | 153 unit + 90 integration + 29 contract + 7 architecture + 6 recovery + 3 concurrency = 288/288 |
| Secret scan full | verde; nenhum valor impresso |
| Frontend tree | `81cff79d9f80de96034f6188ad243f8fecdf526f`, herdado de `origin/develop` (`ea7bd2e`) e sem alteração pela missão de governança |
| Docker após o gate | zero processo; zero container, volume ou network `com.harness.managed=true` |

O OpenAPI drift, o dogfood de receipt/evaluator, os behaviors SQLite/PostgreSQL, o recovery de
receipt e o benchmark OMP fake fazem parte dos 288 testes. Nenhuma credencial ou valor de segredo
foi usado ou incluído nesta evidência.
