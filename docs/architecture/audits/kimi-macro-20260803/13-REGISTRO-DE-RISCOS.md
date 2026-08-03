# 13 — Registro de riscos e achados

> 25 achados, classificados por **tipo** (defeito ≠ risco arquitetural ≠ dívida ≠ lacuna de
> observabilidade ≠ otimização ≠ funcionalidade futura) porque essa distinção é o que permite
> estimar prazo.

---

## Resumo por severidade e tipo

| | CRITICAL | HIGH | MEDIUM | LOW | Total |
|---|---|---|---|---|---|
| DEFECT | — | 3 | 1 | — | 4 |
| ARCHITECTURAL RISK | 2 | 3 | 5 | — | 10 |
| TECH DEBT | — | 2 | 3 | 1 | 6 |
| OBSERVABILITY GAP | — | 1 | 3 | 2 | 6 |
| FUTURE FEATURE | — | — | — | 1 | 1 |
| **Total** | **2** | **9** | **12** | **4** | **27** |

**Bloqueiam piloto: 3.** **Bloqueiam autonomia real: 7.**

---

## CRITICAL

### `F-01` — A Chief é single point of failure

| Campo | Valor |
|---|---|
| **Tipo** | ARCHITECTURAL RISK |
| **Componente** | `ConversationChiefAgentExecutor`, `~/.harness/agent-accounts.json` |
| **Descrição** | Um único alias (`chief-claude-primary`) declara o papel `chief-orchestrator`. E o seletor da Chief (`ResolveChiefAccount`, linha 207) **não usa o `AgentAccountScheduler`**: não consulta cota, cooldown, concorrência nem o ledger de disponibilidade |
| **Evidência** | Código lido linha a linha; `agent-accounts.json` com 7 contas, 1 com o papel |
| **Impacto** | Cota esgotada da conta principal **para o produto inteiro**. É exatamente a situação atual do proprietário |
| **Reprodução** | Esgotar a cota de `chief-claude-primary` e mandar uma mensagem no chat |
| **Causa raiz** | Comprovada: caminho de seleção paralelo, criado antes do scheduler e nunca migrado |
| **Recomendação** | (a) 🟢 adicionar `chief-orchestrator` a `worker-claude-secondary`; (b) 🟡 trocar `ResolveChiefAccount` por `AgentAccountScheduler.Select()` |
| **Bloqueia piloto?** | Não (o dono opera a conta) |
| **Bloqueia autonomia?** | **SIM** |

### `F-03` — O Conselho consome o próprio elenco de críticos

| Campo | Valor |
|---|---|
| **Tipo** | ARCHITECTURAL RISK |
| **Componente** | `AgentCouncilPolicy` + `AgentRoles.CriticDefaultScopes` + `SelectCriticAliases` |
| **Descrição** | O parecer do Conselho é um card comum que só pode ser escrito por papel `critic` (é quem tem o claim `docs/conselho/**`). Cada assento consome uma conta critic como **ator**, e a revisão desse assento exige **outra** conta critic. Com N contas critic, o Conselho exige N≥2 e não paraleliza |
| **Evidência** | `model_invocations`: os 6 assentos rodaram em `worker-antigravity-review`; `attempt_events` 18:06:03Z: `critic.none_available` |
| **Impacto** | **A prova limpa não sai da fase 4.** O Conselho é o portão antes do Desenvolvimento |
| **Causa raiz** | Comprovada: regressão de papel — quem critica precisa ser criticado por quem critica |
| **Recomendação** | Decidir uma das três: (a) isentar cards de tipo `council` da revisão por par; (b) aceitar qualquer papel ≠ ator como revisor de parecer; (c) exigir ≥3 contas critic. Ver doc 17 |
| **Bloqueia piloto?** | **SIM** |
| **Bloqueia autonomia?** | **SIM** |

---

## HIGH

### `F-02` — Três seletores de conta com regras diferentes
**ARCHITECTURAL RISK** · Chief usa `ResolveChiefAccount`, crítico usa `SelectCriticAliases`,
o resto usa `AgentAccountScheduler`. Só o terceiro é testado e explicável. Uma correção de
política de cota precisa ser feita três vezes — e já foi esquecida em duas.
**Bloqueia autonomia: SIM.**

### `F-04` — A Bruna nunca recebe `governance/core.md`
**DEFECT + OBSERVABILITY GAP** · `LoadGovernanceCore()` lê
`<ControlledRoot>/governance/core.md` = `/Users/mateus/Documents/governance/core.md`, que não
existe. Ela roda com o `GovernanceFallback` de 6 linhas em vez das 104 do canon, **sem log**.
**Reprodução:** `ls /Users/mateus/Documents/governance/core.md` → não existe.
**Correção:** apontar para a raiz do repositório do Poseidon **e** logar a degradação.

### `F-05` — Modelo e effort nunca chegam à CLI
**TECH DEBT** · `preferredModel: null` → `SelectedModel: null` → sem `--model`; `Effort` nunca
atribuído no chief loop → sem `--effort`. Banco e painel mostram `opus`/`medium`; a execução usa
o default do binário. O sistema não responde de forma centralizada qual modelo cada papel usa.

### `F-06` — Refatoração de desfecho tipado feita pela metade
**TECH DEBT** · `ExternalFailureKind` existe e é bem desenhado, mas 10 classificadores por
substring sobrevivem em caminho de decisão (`CardCircuitBreakerService` 212-230,
`ChiefBacklogLoopService` 2852/2858, `AgentRunOrchestrator` 2254/2399/2402). É a mesma família
de defeito que custou 1h40 em 03/08.

### `F-12` — Falta de revisor escala o card em 15 min e o prende para sempre
**DEFECT** · 4 adiamentos × 5 min → `escalated`; e o replanejamento — único caminho de volta —
recusava tentativa em `awaiting_review` por estado inválido.
**Evidência:** 6 cards `escalated/blocked` desde 18:06Z de 03/08.
**Conserto existe no working tree, NÃO commitado nem provado** (`ReviewerShortageGrace = 2h`,
`CriticRosterHasCandidate`, `WorkAttemptIsReplannable` negando só `running`/`approved`).

### `F-13` — Chaves de assento do Conselho que não existem no catálogo
**DEFECT** · `AgentCouncilPolicy` usa `playbook-po` e `playbook-sre-devops`;
`CanonicalAgentDefinitions` tem `playbook-product-owner`, `playbook-devops`,
`playbook-sre-sustentacao`. **Medido:** o assento de Product Owner foi executado pela persona
**Software Architect** — a lente que menos deveria se repetir numa mesa que já tem um Arquiteto.
Não há teste que ligue as duas listas.

### `F-16` — Fases 6–9 são documentais com gate humano
**ARCHITECTURAL RISK** · A fase 5 tem portão factual forte (`Merged` + integração verde +
review distinta). As fases 6 a 9 exigem **documentos**, não execução medida de suíte, pentest,
UAT ou rollback. Não é defeito; é maturidade. **Não pode ser apresentado como "a fábrica testa,
homologa e faz release sozinha".**

### `F-20` — Não existe coordenador de recurso pesado
**ARCHITECTURAL RISK** · `AutoDispatchMaxConcurrent` limita **tentativas**, não builds. 7 agentes
podem disparar 7 `dotnet build`. Hoje mitigado por disciplina humana. Numa instalação de cliente,
derruba a máquina. **Bloqueia piloto de terceiro: SIM.**

---

## MEDIUM

| ID | Tipo | Título |
|---|---|---|
| `F-04-B` | TECH DEBT | `docs/agents/bruna.md` (136 linhas, declarado `sourceOfTruth` no manifesto) não é aberto por código nenhum — **DECORATIVO** |
| `F-07` | OBSERVABILITY GAP | A explicação de inelegibilidade existe e é boa (`AccountSelectionDecision` carrega todos os candidatos com motivo), mas morre no log: não chega ao card nem à tela. O dono vê "Precisa de atenção" e não vê por quê |
| `F-08` | ARCHITECTURAL RISK | A continuidade conversacional da Bruna depende do `SessionId` da CLI do fornecedor. Trocar de provedor perde o histórico do modelo |
| `F-09` | TECH DEBT | `worker-kimi-ui` está habilitada na frota e nunca poderá executar (`kimi-code` sem adapter, sem capability `review`). Deveria nascer visivelmente `Unavailable`, não elegível-e-recusada |
| `F-14` | ARCHITECTURAL RISK | As 6 lentes do Conselho rodaram no mesmo modelo, mesma conta, mesmo effort — a diversidade é só de prompt |
| `F-17` | TECH DEBT | Dois sistemas de escopo: `AllowedScopes`/`DeniedScopes` declarados por persona (35 valores) e `AgentPathScopeKind` imposto por papel (4 valores). **Só o segundo é vinculante** |
| `F-19` | ARCHITECTURAL RISK | Contenção desligada. Justificada e reversível para uso interno; para produto vendido a terceiro é risco aberto |
| `F-22` | OBSERVABILITY GAP | 73% do custo é retrabalho e a taxa de aceite de primeira é 33%. Não há classificação da **causa** da rejeição — impossível distinguir contexto insuficiente de crítico severo |
| `F-24` | ARCHITECTURAL RISK | Backoffs e contadores de adiamento vivem em memória do processo (`_reviewBackoff`, `_noProgressRuns`, `_dispatchBackoff`, `_reviewInfrastructureFailures`). Reiniciar o Host zera o histórico que justificaria escalar |
| `F-25` | ARCHITECTURAL RISK | A supervisão da operação vive fora do produto, em scripts shell e num binário separado, atado à sessão de agente que o iniciou. Um vigia já morreu junto com a sessão e o binário ficou defasado por horas |
| `F-26` | OBSERVABILITY GAP | O custo real é desconhecido: Antigravity grava `usage_unknown`/`output_tokens=0` e é a única conta critic viva. `METRICS.json` (US$ 302,96) **subestima** |

---

## LOW

| ID | Tipo | Título |
|---|---|---|
| `F-10` | TECH DEBT | Antigravity não declara `ExternalFailureKind` — falhas dela voltam à heurística de substring |
| `F-11` | FUTURE FEATURE | Criação de persona **nova** pela Bruna: caminho CRUD existe, uso não comprovado |
| `F-15` | OBSERVABILITY GAP | `council.incomplete` descreve o sintoma e esconde a causa; a mesma mensagem apareceu para path scope negado e para ausência de revisor |
| `F-18` | OBSERVABILITY GAP | A revisão é o único trabalho de agente que não é card. Existe em `work_reviews`, mas não aparece no quadro — o custo do crítico não entra na contabilidade |
| `F-21` | OPTIMIZATION | `RagContextProvider.SearchAsync` carrega o corpus inteiro em memória. Irrelevante com 13 documentos; problema com 13.000 |
| `F-23` | OBSERVABILITY GAP | 78.363 mensagens em `outbox_messages` contra 80.490 eventos de ledger — o outbox parece estar sendo usado como log. Não há consulta pronta de idade da fila ou taxa de entrega |

---

## Achados FAVORÁVEIS (registrados porque a auditoria não é só de problemas)

| # | O que foi verificado |
|---|---|
| ✅ | **Nenhum bypass encontrado** para "o modelo declara pronto" — nos três níveis (card, fase, projeto) |
| ✅ | A independência actor≠critic é imposta em **duas camadas** (scheduler e agregado de domínio) |
| ✅ | O `ContextBundleBuilder` bloqueia bundle com segredo e com conflito canônico, e registra truncation |
| ✅ | O circuito por card tem contagem **derivada e idempotente** — reprocessar dá o mesmo resultado |
| ✅ | Recuperação de reinício do Host e morte de worker **provada ao vivo**, sem punir o card |
| ✅ | Cota tipada com instante de reset lido do próprio provedor, provada em produção às 13:18Z |
| ✅ | Segredos nunca em disco do produto — só referência `keychain://` |
| ✅ | `AGENTS.md`/`CLAUDE.md` gerados de `governance/` com checksum, e consumidos pela CLI |
| ✅ | Documento inválido não consome um crítico (validação determinística pré-review) |
| ✅ | Zero referências a `TrensRJ` no repositório |
| ✅ | Configuração de contas inválida **não** degrada em silêncio |
| ✅ | Personas declaram `NonActivationCriteria` — raro e valioso |
