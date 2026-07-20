---
id: prompt-backend-continuation-v2-0
version: "2.0"
status: historical
createdAt: 2026-07-18T21:33:15-03:00
source: legacy-external-import
supersedes: [prompt-backend-mission-v1-3]
checksum: sha256:3fc1585e07405607c6b5dbbd1a893e316de70b8a26696e83e1fc12f1898c393a
originalChecksum: sha256:56a077c6346c12ffffc1342cd8723dc99d772ebec542b28055754e29fe1910bf
---

> Histórico sanitizado e nunca carregado automaticamente. Procedência: arquivo legado `PROMPT_CONTINUACAO_FABLE_BACKEND_POSEIDON_v2.0.md`; hash do original registrado no frontmatter. Caminhos locais foram substituídos por `$REPO_ROOT`, `$POSEIDON_FRONTEND_CLONE` ou caminhos relativos versionados. Este prompt foi substituído pela governança canônica vigente.

# CONTINUAÇÃO FABLE — BACKEND E PLATAFORMA POSEIDON
## Handoff autônomo, idempotente e com padrão rígido de C# v2.0

Você está assumindo um projeto existente, desenvolvido por sessões anteriores do Codex. Você começa sem contexto de conversa, mas o repositório possui código, testes, migrations, evidências e documentação viva suficientes para retomada.

Não reconstrua o projeto. Não repita fases verdes. Não crie uma arquitetura paralela. Não substitua implementações válidas apenas por preferência pessoal.

## 1. Fontes de verdade

Use, nesta ordem:

1. `governance/prompts/backend-mission-v1.3.md`.
2. Este handoff, para o estado atual, ponto de retomada e regras adicionais de C#.
3. Código, testes, migrations, commits e comportamento executado.
4. Documentação viva:
   - `docs/backend/execution/CURRENT_STATE.md`
   - `docs/backend/execution/PROGRESS.md`
   - `docs/backend/execution/MASTER_PLAN.md`
   - `docs/backend/execution/RISKS.md`
   - `docs/backend/execution/DECISIONS_PENDING.md`
   - `docs/contracts/CONTRACT_RECONCILIATION.md`
   - `docs/frontend/CURRENT_STATE.md`
   - `docs/frontend/HANDOFF_API.md`
   - `docs/backend/execution/evidence/`

Em divergência, valide pelo Git, código, migrations e testes. Corrija a documentação e registre ADR se houver mudança arquitetural.

## 2. Repositório, clones e branches

Repositório oficial:

`https://github.com/mateusdomi/harness-poseidon.git`

Clone exclusivo do backend:

`$REPO_ROOT`

Clone utilizado pelo Kimi:

`$POSEIDON_FRONTEND_CLONE`

Trabalhe exclusivamente no clone backend.

Somente `main` e `develop` são permitidas. Trabalhe em `develop`. Não crie feature branch, task branch, release branch, hotfix ou worktree adicional no repositório oficial. Nunca use force push e nunca faça merge em `main`.

## 3. Coordenação com o frontend

O Kimi é proprietário exclusivo de:

- `frontend/**`
- `docs/frontend/**`

Você não pode editar esses diretórios.

No último registro, FE-0 a FE-4 estavam concluídas localmente pelo Kimi, com 270 testes, build, Storybook e E2E/a11y verdes, mas o commit/push final ainda poderia estar pendente.

Portanto:

1. não aguarde para executar trabalho backend independente;
2. faça `git fetch origin` antes de cada integração;
3. não presuma que mudanças não publicadas do clone do Kimi estão disponíveis;
4. não copie arquivos manualmente da working tree do Kimi;
5. após o push final do frontend, rebase `origin/develop`;
6. registre drift em `docs/contracts/CONTRACT_RECONCILIATION.md`;
7. não altere contratos do frontend silenciosamente.

Pode alterar:

- `src/**`
- `tests/**`
- `infra/**`
- `tools/backend/**`
- `docs/backend/**`
- `docs/contracts/**`
- `docs/architecture/**`
- `docs/decisions/**`
- `docs/security/**`
- `docs/testing/**`
- arquivos .NET compartilhados da raiz, após inspeção

## 4. Estado conhecido

Último commit funcional backend informado:

`56388406554752a013f9907212d0cde6c92006cd`
`feat(execution): stream codex through docker sandbox`

Commit de documentação subsequente:

`bf1865df7a18b101d1a290f114c4a56e4ed8a919`
`docs(execution): record isolated codex session`

Estado registrado:

- Fase 0 concluída.
- GNG-1 verde.
- Fase 1 concluída.
- GNG-2 formalmente verde.
- Fase 2 em andamento.
- Grande parte do MVP pessoal já está verde.
- 176/176 testes backend verdes.
- 270/270 testes frontend verdes no gate integrado.
- Build Release sem warnings e sem erros.
- Sandbox Docker streaming validado.
- `CodexCliAgentExecutor` executou turno estruturado em container.
- Zero recurso Docker órfão.
- Próximo incremento: `F2-DOGFOOD-1d.2`.

Audite o remote atual antes de continuar; contagens maiores são válidas.

## 5. Protocolo de retomada

Execute:

```bash
cd "$REPO_ROOT"
git status --short
git branch --show-current
git remote get-url origin
git fetch origin
git log --oneline --decorate -15
git worktree list
```

Depois:

1. confirme `develop`;
2. preserve trabalho local válido;
3. com working tree limpa, execute `git rebase origin/develop`;
4. nunca resolva conflito apagando trabalho do Kimi;
5. confirme que somente `main` e `develop` existem;
6. inventarie Docker:

```bash
docker ps -a --filter label=com.harness.managed=true
docker volume ls --filter label=com.harness.managed=true
docker network ls --filter label=com.harness.managed=true
```

7. leia os documentos da seção 1;
8. inspecione os commits `5638840` e `bf1865d`;
9. execute:

```bash
tools/backend/verify.sh
```

Baseline esperado:

- pelo menos 176 testes backend verdes;
- build Release sem erros;
- zero warnings;
- frontend preservado;
- nenhum recurso Docker órfão.

Se o repositório já tiver avançado além de `F2-DOGFOOD-1d.2`, retome do primeiro incremento incompleto comprovado.

## 6. Ponto de retomada — F2-DOGFOOD-1d.2

Objetivo:

**Persistir claims de escopo e o catálogo de workspace por tentativa; depois compor worktree exclusiva, sessão Docker e `CodexCliAgentExecutor` para um projeto externo.**

A implementação atual já provou sessão app-server streaming em Docker, worktree em `/workspace`, `CODEX_HOME` isolado, proxy-only egress, rootfs read-only, limites, shutdown gracioso, cleanup e executor estruturado.

A lacuna é de durabilidade. Claims e catálogo de workspace não podem permanecer apenas em memória.

### 6.1 Modelo durável

Modele explicitamente:

- ClaimId;
- TenantId;
- ProjectId;
- BusinessTaskId;
- BusinessAttemptId;
- TechnicalExecutionId;
- repositório externo;
- raiz controlada;
- branch;
- caminho da worktree;
- scopes normalizados;
- owner;
- fencing token;
- lease;
- heartbeat;
- lifecycle do claim;
- lifecycle do workspace;
- timestamps UTC;
- versão de concorrência;
- erro final sanitizado;
- estado de cleanup;
- idempotency key.

Não reutilize `runner_attempts` como autoridade de domínio.

Estados devem ser fechados e transições explícitas. Não use strings livres.

### 6.2 Invariantes

- claim ativo não colide com claim ancestral, descendente ou idêntico incompatível;
- claims disjuntos podem coexistir;
- somente owner e fencing token correntes podem renovar, alterar ou liberar;
- claim expirado pode ser recuperado deterministicamente;
- uma tentativa possui no máximo um workspace ativo;
- branch e worktree pertencem à tentativa correta;
- paths permanecem dentro da raiz controlada;
- worktree não registrada nunca é removida;
- branch divergente impede cleanup;
- retry com a mesma idempotency key não duplica claim, workspace, branch ou worktree;
- restart não perde exclusões;
- falha entre claim, branch, worktree, sandbox e executor é reconciliável;
- cleanup é repetível;
- claim só é liberado após término ou abandono e política de cleanup.

### 6.3 Persistência

Siga os padrões existentes de migrations, Inbox, Outbox, ledger, OCC, leases e fencing. Não introduza ORM, banco ou mecanismo paralelo.

Filesystem e Git não são transacionais com SQL. Use máquina de estados e reconciliação compensatória; não finja atomicidade distribuída.

Operações críticas:

- reservar claim;
- registrar workspace planejado;
- registrar branch/worktree criada;
- marcar sandbox iniciada;
- registrar sessão do executor;
- heartbeat/checkpoint;
- conclusão/falha;
- cleanup;
- liberação do claim;
- auditoria e eventos.

### 6.4 Composição da tentativa

Crie um application service/orchestrator tipado que:

1. valide projeto externo e política;
2. adquira claim persistente;
3. crie ou recupere workspace;
4. crie branch/worktree;
5. persista estado observado;
6. abra `ISandboxProvider`;
7. inicie `CodexCliAgentExecutor`;
8. renove lease/heartbeat;
9. persista session/thread/turn IDs apenas como metadados;
10. persista checkpoints e evidências;
11. conclua ou falhe;
12. encerre sandbox;
13. faça cleanup;
14. libere claim;
15. publique eventos;
16. registre auditoria.

A sessão do provider nunca é fonte de verdade.

### 6.5 Recuperação

Teste encerramento abrupto:

- após claim e antes da branch;
- após branch e antes da worktree;
- após worktree e antes do sandbox;
- durante o executor;
- após conclusão e antes do cleanup;
- durante cleanup parcial;
- durante liberação do claim.

Após restart:

- reconciliar estágio;
- continuar ou compensar;
- não duplicar branch/worktree;
- não perder claim ativo;
- não liberar claim de outro owner;
- não remover recurso fora da raiz;
- não deixar Docker órfão;
- preservar ledger, evidências e sequência.

### 6.6 Testes e gate

Testes comuns usam fake/fixture e não consomem cota real.

Inclua:

- unitários de invariantes;
- integração SQLite;
- PostgreSQL quando o padrão atual exigir;
- concorrência;
- fencing;
- idempotência;
- recovery;
- fixtures Git descartáveis;
- Docker real somente em testes classificados;
- contract/OpenAPI/event tests;
- arquitetura;
- cleanup.

Entregue código, migrations, testes, evidência, documentação, commit, rebase, repetição dos testes e push normal em `develop`.

## 7. Depois de F2-DOGFOOD-1d.2

Executar dogfood completo:

1. solicitação;
2. Chief;
3. demanda/tarefa;
4. claim;
5. branch/worktree;
6. sandbox;
7. Codex executor;
8. alteração em projeto fixture;
9. build/test;
10. evidência;
11. critic/gate;
12. conclusão;
13. cleanup;
14. auditoria ponta a ponta.

Depois:

- integrar frontend final publicado;
- executar E2E contra API real;
- preparar validação humana do GNG-3;
- não declarar GNG-3 homologado sem aceite humano;
- continuar o roadmap original.

# 8. PADRÃO OBRIGATÓRIO DE C# FORTEMENTE TIPADO

## 8.1 Uso correto de tipos

`record` não é proibido e é fortemente tipado.

Use:

- `class` para aggregates e entities com identidade, versão e lifecycle;
- `sealed record` para DTOs, comandos, resultados e eventos imutáveis;
- `readonly record struct` para value objects pequenos;
- enums ou hierarquias fechadas para estados finitos;
- interfaces apenas quando existe abstração real.

Não converta classes de domínio existentes em records por estilo.

## 8.2 Proibições

Não usar em contratos públicos, domínio ou application layer:

- `dynamic`;
- `ExpandoObject`;
- `object` para payloads de domínio;
- `Dictionary<string, object?>`;
- `Dictionary<string, dynamic>`;
- tuplas como contrato público;
- anonymous types persistidos ou enviados como evento;
- `JsonElement`, `JsonDocument` ou JSON bruto atravessando camadas;
- strings livres para status, tipo, severidade ou modo;
- IDs indiscriminadamente como `string`;
- casts frágeis;
- reflection para contornar tipos;
- `null!` como correção;
- entities EF como resposta HTTP.

JSON bruto só pode existir em adapter de borda inevitável e deve ser convertido imediatamente em tipo conhecido.

## 8.3 Contratos nomeados

Toda operação deve ter tipos nomeados, por exemplo:

- `AcquireScopeClaimCommand`;
- `ScopeClaimSnapshot`;
- `CreateAttemptWorkspaceCommand`;
- `AttemptWorkspaceSnapshot`;
- `StartIsolatedExecutionCommand`;
- `IsolatedExecutionResult`;
- `WorkspaceLifecycleChangedEvent`.

Não use `object payload`, dicionários ou tuplas.

Records posicionais só para tipos pequenos e inequívocos. Contratos maiores usam propriedades nomeadas `required`, factory ou classe/record nominal legível.

## 8.4 Domínio

- factories e métodos de domínio criam estado válido;
- transições ocorrem por métodos semânticos;
- setters de lifecycle não são públicos;
- invariantes no domínio e no banco quando aplicável;
- FluentValidation não substitui regra de negócio;
- value objects para IDs, paths, branches, hashes, tokens, versões e idempotency keys quando agregarem segurança;
- evitar primitive obsession.

## 8.5 API e serialização

- DTOs separados de entities;
- OpenAPI a partir de contratos concretos;
- Problem Details consistente;
- JSON camelCase;
- enums com parsing definido;
- datas `DateTimeOffset` UTC;
- custos com `decimal`;
- payload concreto por evento;
- nenhum `object Payload`;
- testes de round-trip JSON;
- source generation opcional se reduzir drift sem complicar.

## 8.6 Nullability e erros

- nullable reference types habilitado;
- zero warnings;
- warnings como erro;
- não usar null-forgiving como padrão;
- falhas esperadas usam `Result<T>`/erro de domínio;
- exceptions para situações excepcionais;
- não expor segredo, stack trace ou path sensível.

## 8.7 Configuração e DI

- `IOptions<T>`;
- `ValidateOnStart`;
- sem service locator;
- sem `IServiceProvider` no domínio;
- lifetimes coerentes;
- singleton não captura scoped;
- cancellation token em I/O;
- `IClock` para tempo;
- usar abstrações existentes para filesystem, processo, Git, Docker e secrets.

## 8.8 Persistência

- mappings explícitos;
- constraints e índices provider-specific;
- migrations SQLite/PostgreSQL separadas;
- OCC explícita;
- transações curtas;
- sem lazy loading implícito;
- queries tenant-scoped;
- sem SQL concatenado com input;
- stores reidratam tipos válidos ou falham explicitamente.

## 8.9 Testes de tipagem

Adicione ou amplie testes de arquitetura que falhem se contratos públicos expuserem:

- `dynamic`;
- `object`;
- `JsonElement`;
- `JsonDocument`;
- `Dictionary<string, object?>`;
- tuplas públicas;
- entities de persistência em endpoints.

Use allowlist mínima e documentada apenas para adapters inevitáveis.

Teste também:

- round-trip JSON;
- estados e transições;
- parsing inválido;
- OpenAPI drift;
- eventos;
- constraints;
- concorrência;
- idempotência;
- recovery.

Não faça refatoração massiva de records existentes apenas por preferência. Altere somente quando houver risco real, violação ou teste falhando.

## 9. Evidência

Para cada fatia:

1. implementar;
2. format;
3. build Release;
4. testes focados;
5. suíte integral;
6. zero warnings;
7. evidência;
8. atualizar documentação;
9. commit;
10. fetch + rebase;
11. repetir testes afetados;
12. push normal.

## 10. Encerramento

Antes de encerrar:

- working tree consistente;
- testes verdes;
- zero warnings;
- commit;
- rebase;
- push;
- CURRENT_STATE com SHA remoto;
- próximo passo exato;
- migrations sem estado parcial;
- processos encerrados;
- zero Docker órfão;
- nenhuma fixture/worktree restante;
- frontend preservado.

Comece auditando o estado remoto e retome por `F2-DOGFOOD-1d.2`, salvo se o repositório comprovar avanço posterior.
