# Relatório de Validação do Poseidon

> **ADENDO — 30/07/2026, segunda rodada (correção autônoma).**
> Após a auditoria, o dono autorizou corrigir tudo de forma autônoma. Esta segunda rodada mudou o
> veredito de várias fases. O corpo do relatório abaixo é o **diagnóstico original**, preservado
> como registro; a seção **"Correções aplicadas na segunda rodada"**, no fim, tem o estado atual.
>
> Resumo do que mudou: **F3, F6, F12, F13 e F14 saíram de parcial/reprovada** — as políticas foram
> ligadas ao fluxo de produção, o protocolo de mensagens foi unificado numa fonte só, e a
> criticidade passou a ser estimada de verdade. **F16 e F17 continuam reprovadas.** Uma correção
> foi **revertida deliberadamente** após medir que quebrava o produto (detalhe na seção).
>
> Uma correção factual ao diagnóstico original, encontrada ao implementar: a afirmação de que a
> migration `0098` era "tabela sem store" estava **errada**. Os stores `SqliteChiefLoopGuardStore`
> e `PostgresChiefLoopGuardStore` existem e estão no DI desde a campanha — minha busca inicial usou
> o nome do arquivo de migration (`chief_loop_guards`) em vez dos nomes reais das tabelas
> (`chief_self_triggered_turns`, `chief_causal_edges`, `chief_loop_interruptions`). O que faltava
> era só a **decisão**: ninguém chamava a política. Isso agora está ligado.

## Resumo executivo

- **Documento de referência:** `~/Desktop/execucao-codex/Execucao-agentes/poseidon-arquitetura-FINAL-v6.md` (17 fases, Partes I–IV) + `poseidon-orientacao-agentes.md`
- **Data da validação:** 30/07/2026
- **URL validada:** `http://127.0.0.1:5173/` (confirmada por requisição real: `/health` → `200 {"status":"healthy"}`, SPA → `200`)
- **Ambiente:** local / `personal-session` · macOS arm64 · .NET 10 (SDK vendorizado) · branch `develop` @ `b005e529`
- **Banco utilizado:** SQLite `~/.harness-poseidon/harness.db` (31 MB, 86 migrations aplicadas). Postgres espelhado não exercitado nesta auditoria.
- **Total de fases:** 17
- **Fases aprovadas:** 9 — F1, F2, F4, F5, F7, F8, F9, F10, F11
- **Fases parcialmente aprovadas:** 6 — F3, F6, F12, F13, F14, F15
- **Fases reprovadas:** 2 — F16, F17
- **Fases bloqueadas:** 0 (nenhuma validação ficou impedida: a indisponibilidade inicial do Docker foi resolvida e os testes dependentes foram reexecutados)
- **Defeitos encontrados:** 10 (4 altos, 3 médios, 3 baixos)
- **Defeitos corrigidos:** 2 altos — DEF-01 (acessibilidade séria em `/delivery`, com teste de regressão no gate que efetivamente roda) e DEF-00 (ausência de gate para política sem consumidor, agora automático)
- **Pendências restantes:** 8 (nenhuma bloqueadora do uso do produto; 2 são fases inteiras a implementar)

### O achado estrutural que explica quase todo o resto

A campanha inteira foi executada sob um gate que **nunca rodou testes de navegador**. `tools/backend/verify.sh` delega ao frontend `npm run check`, que é `lint + typecheck + gate:vocabulary + vitest` — `grep -c playwright tools/backend/verify.sh` = **0**, e nenhum workflow de CI invoca a suíte. Consequência medida: 9 specs Playwright falhavam sem que nenhuma fase fosse reprovada por isso, e **um defeito sério de acessibilidade sobreviveu à campanha inteira** (corrigido nesta auditoria, item DEF-01).

O segundo achado estrutural é de natureza diferente e mais grave para o produto: **14 políticas de backend existem, estão testadas e não são chamadas por nenhum caminho de produção**. O gate de cada fase passou porque os testes unitários da política passam — mas a política nunca executa. Isso é o que o pedido chama de "existe no código, mas não funciona em execução real", e é a causa da reprovação de F16 e F17 e das ressalvas de F12/F13/F14.

Esse achado foi convertido em **gate automático** nesta auditoria (`tests/Harness.ArchitectureTests/PolicyWiringTests.cs`): política estática sem consumidor passa a reprovar o build, com a dívida atual declarada linha a linha. Ao ser executado, o gate encontrou **duas órfãs a mais** do que o levantamento manual havia identificado — `DispatchAuthorityPolicy` (que meu grep contabilizara por aparecer dentro de um comentário XML) e `TurnCompletionPolicy`, que é justamente a tarefa (2) da F13, "conclusão exatamente uma vez". É a demonstração prática de por que o gate precisa existir: revisão humana erra nessa conta, gate não.

### Método usado (por que estes vereditos são confiáveis)

Nenhuma fase foi aprovada por existir arquivo, classe ou commit. Cada veredito combina:

1. **Wiring real** — para cada política, contagem de referências em `src/` excluindo o próprio arquivo, `bin/` e testes. Zero referências de produção = código morto, independentemente da cobertura de testes.
2. **Execução real na API** — projetos, documentos, aprovações, exportação e prototipação exercitados por HTTP contra o Host no ar, com dados `QA-POSEIDON-2026-07-30-*`.
3. **Execução real no navegador** — Playwright contra o Host (não contra o mock MSW), com login real, medindo axe (WCAG 2.1 AA), erros de console, respostas HTTP ≥ 400, vazamento de léxico e overflow horizontal.
4. **Requisitos de tela derivados literalmente da arquitetura** — 24 asserções, uma por exigência textual das fases de frontend.

Instrumentos e evidências: `artifacts/qa-poseidon/`.

---

## Defeitos encontrados (consolidado)

| # | Severidade | Fase | Defeito | Situação |
|---|---|---|---|---|
| DEF-00 | **Alta** | Transversal | Nenhum gate detectava política sem consumidor: "testes verdes" e "código executa" eram indistinguíveis para a campanha inteira | **CORRIGIDO** — novo gate de arquitetura |
| DEF-01 | **Alta** | F5 | `/delivery`: 8 violações axe `definition-list` + 48 `dlitem` (impacto *serious*) — `dl > div > div > dt` deixa rótulos órfãos para leitores de tela | **CORRIGIDO** + teste de regressão |
| DEF-02 | **Alta** | F16 | Fase inteira sem fiação: `MastTaxonomy`, `PassAtKPolicy`, `LearningPromotionPolicy` sem consumidor; sem migration (faixa 0105–0107 vazia); sem painel | Aberto |
| DEF-03 | **Alta** | F17 | Fase inteira sem fiação: `ContinuousReviewPolicy`, `WorktreeHookPolicy`, `PersonaDefinitionLint` sem consumidor; sem migration (0108–0109 vazia); executores CLI sem pareamento | Aberto |
| DEF-04 | Média | F12 | `AgentWaitPolicy`, `ChiefLoopGuardPolicy`, `PlanDepthPolicy` órfãs; migration `0098_chief_loop_guards` cria tabela que **nenhum store lê ou escreve** | Aberto |
| DEF-05 | Média | F14 | `NegativeBoundaryPolicy` órfã; `LayeredVerificationPolicy.Evaluate` (as 3 camadas) nunca chamado — só `MayOccupyReviewer`; `EffortPolicy.SelectFanOut` e `RouteModel` nunca chamados | Aberto |
| DEF-06 | Média | F6 | `criticality` nunca é estimada (sempre `"medium"`, default de `Project.cs:80`) e `technologies` nunca é inferida (sempre `[]`) — contraria D12 | Aberto |
| DEF-07 | Baixa | F13 | Runtime aceita 3 tipos de mensagem (`heartbeat`, `checkpoint`, `completion`); a fase exigia 7. `escalation`, `decision_gate` e `merge_ready` não existem em execução | Aberto |
| DEF-08 | Baixa | Transversal | `tailwind.config.js` declara só `md/lg/xl`: **42 ocorrências de `sm:` em 29 arquivos não geram CSS algum** | Aberto (1 arquivo corrigido) |
| DEF-09 | Baixa | F9/API | `POST /documents/{id}/transitions` com `toState=awaitingApproval` responde **200 com o recurso inalterado** (no-op deliberado), enquanto transição inválida análoga responde 409 | Aberto (por decisão) |

---

## Fase 01 — Léxico, modos de apresentação e gate de vocabulário

### Requisitos previstos
- Aplicar o léxico §2 a todos os namespaces do modo Negócio.
- Corrigir chaves cruas vazando; **chave ausente deve falhar o build**.
- Gate de CI Default-FAIL com lista de termos proibidos.
- `usePresentationMode()` único, padrão = Negócio.

### Evidências encontradas no código
- Gate: `frontend/scripts/check-business-vocabulary.mjs` (337 linhas) — Default-FAIL explícito: namespace novo não classificado é tratado como Negócio; classificação apontando para namespace inexistente é violação; exceções enumeradas em `EXEMPTIONS` **com motivo declarado**.
- Fail-fast de chave: `frontend/src/i18n/index.ts:72-75` (`saveMissing` + `missingKeyHandler` com `throw`), teste `src/i18n/__tests__/i18n-missing-keys.test.ts`.
- Hook: `usePresentationMode` referenciado em 23 arquivos.
- Wiring do gate: `npm run gate:vocabulary` dentro de `npm run check` → `verify.sh`.

### Validação em execução
- **Cenário:** navegação autenticada no Host real, modo Negócio (padrão).
- **Resultado obtido:** menu sem nenhum termo proibido (16 itens); `gate:vocabulary` verde no `verify.sh` desta auditoria.

### Testes automatizados
Existentes: teste de chave ausente + cobertura do gate. Criados: nenhum (sem lacuna).

### Problemas encontrados
1. **Limite de escopo do gate (achado, não defeito):** ele varre o **catálogo i18n**, não a tela renderizada. Isenções como `orchestrator.definitions.*` afirmam "só aparece no modo Técnico" — afirmação verdadeira hoje, verificada por leitura de código, mas **não protegida por código**. Se uma tela passar a renderizar essa subárvore no Negócio, o gate continua verde.

### Correções realizadas
Nenhuma necessária.

### Status final
**APROVADA**

### Pendências
- Job de CI dedicado não existe (o board declara que o token do agente não tinha escopo `workflow`); o gate roda via `npm run check`/`verify.sh`, o que preserva o efeito prático.
- Recomendação: complementar o gate com asserção de tela (varredura do DOM renderizado no modo Negócio) para fechar a lacuna acima.

---

## Fase 02 — Voz da Bruna e experiência de chat

### Requisitos previstos
Tom caloroso sem jargão; mapa de tradução de reason codes; identidade fixa "Bruna Magalhães — Diretora…"; reescrita dos dois fluxos homologados; seletor "Modo de trabalho"; persistir última conversa por perfil+projeto; investigar indicador de loop (D17).

### Evidências encontradas no código
- `src/Modules/Harness.Modules.Conversations/Application/ReasonCodeHumanizer.cs` — mapa código→frase humana, incluindo `account.role_not_allowed`.
- Migration `0090_profile_active_conversation.sql` (persistência da conversa por perfil).
- D17 corrigido em `e24f169c` ("impede turno concluído de reabrir").

### Validação em execução
- **Cenário:** `/chat` no Host real, modo Negócio.
- **Resultados obtidos:** identidade "**Bruna Magalhães**" presente ✓; "Equipe virtual"/"Chief" ausentes ✓; seletor "**Modo de trabalho**" presente ✓; seletor "Modelo" ausente ✓.
- Ocorrência da palavra "migration" na tela foi investigada e **descartada**: vem de *dados* (mensagem escrita pelo próprio usuário e títulos de card do projeto Poseidon, que é um projeto técnico), não de string de interface. O léxico rege a interface, não o conteúdo do cliente.

### Testes automatizados
Existentes: 1305 unitários + 233 de integração cobrem tom, tradução e persistência (conforme board, revalidados pelo `verify.sh` desta auditoria). Criados: nenhum.

### Problemas encontrados
Nenhum defeito confirmado.

### Status final
**APROVADA**

### Pendências
Nenhuma.

---

## Fase 03 — Dashboard de negócio

### Requisitos previstos
Quadro da Equipe como primeiro card; timeline horizontal de etapas com drill-in; Produtividade da equipe; Capacidade da equipe (ex-"Cotas críticas"); **card "Aprovações pendentes" substituindo "Saúde da governança" (D14)**; progresso unificado no Negócio; ordem dos cards.

### Evidências encontradas no código
- `features/cockpit/components/attention-cards.tsx:74` — `PendingApprovalsCard` com contagem (`Badge`) e link para `/documents?tab=approvals`.
- `features/cockpit/pages/cockpit-page.tsx:212` — card renderizado.
- `features/cockpit/hooks/use-cockpit.ts:59-71` — progresso por fase com tratamento explícito de `phase_plan_not_found`.

### Validação em execução
- **Cenário:** `/cockpit` autenticado no Host real.
- **Resultado obtido — títulos renderizados, em ordem:** Quadro da Equipe · Produtividade da equipe · Etapas do projeto · 1-Triagem · Capacidade da equipe · Decisões humanas · Andamento do projeto · Próxima ação recomendada · Distribuição de tarefas · Bloqueios · Atividade recente.
- "Saúde da governança" **ausente** ✓ (D14 cumprida na remoção). "Cotas críticas" ausente ✓.
- 8 respostas `404 phase_plan_not_found` foram investigadas e **descartadas como defeito**: o backend está semanticamente correto ("a etapa ainda não tem plano materializado") e o frontend **já trata** o caso retornando `null`. O log no console é emissão nativa do navegador para qualquer 4xx, fora do alcance da aplicação.

### Testes automatizados
Existentes: `features/cockpit/__tests__/cockpit.test.tsx` cobre `pendingApprovals`. Criados: nenhum.

### Problemas encontrados
1. **Rótulo divergente da D14 (médio-baixo):** o card exige-se "**Aprovações pendentes**"; o produto exibe "**Decisões humanas**" (`pt-BR.json:1079`). Funcionalmente equivale (contagem + link para Documentos), mas o léxico §2 reserva a ideia de "decisão sua" para **escalação** (`agent_requests`), que é outro conceito. O leigo pode ler o card como "a equipe travou" quando na verdade é "há documento esperando seu aval".

### Correções realizadas
Nenhuma aplicada: renomear rótulo de produto homologado é decisão do dono, não do auditor. Registrado como pendência.

### Status final
**PARCIALMENTE APROVADA**

### Pendências
- Decidir entre renomear para "Aprovações pendentes" (texto da D14) ou manter "Decisões humanas" e registrar o desvio como decisão consciente.

---

## Fase 04 — Shell de navegação e seleção global de projeto

### Requisitos previstos
Menu por modo de apresentação; Chat primeiro; Onboarding fora do menu; Aprovações → redirect; `useActiveProject()` global persistido; renomeações do léxico.

### Evidências encontradas no código
`frontend/src/app/navigation.ts` (grupos de menu), shell/header com seletor de projeto.

### Validação em execução
| Requisito | Resultado |
|---|---|
| Chat é o primeiro item | ✓ (1º = "Chat") |
| Onboarding fora do menu | ✓ (16 itens, nenhum de onboarding) |
| Menu sem termo proibido no Negócio | ✓ |
| Seletor global de projeto no cabeçalho | ✓ (3 controles) |
| `/approvals` redireciona | ✓ → `http://127.0.0.1:5173/documents?tab=approvals` |

### Testes automatizados
Existentes: `src/app/navigation.test.ts` + cobertura de menu/paleta (28/28 conforme board). Criados: nenhum.

### Problemas encontrados
Nenhum.

### Status final
**APROVADA**

### Pendências
Nenhuma.

---

## Fase 05 — Central de Entregas + prazo no intake + ZIP de documentos

### Requisitos previstos
Tela no modelo rastreamento (início, prazo, % total, etapa, orientação de risco sem jargão); Bruna pergunta o prazo no intake e registra; **exportação ZIP dos documentos aprovados com manifesto**; migração de funcionalidades para o Assistente de PO.

### Evidências encontradas no código
- `Modules.Documents/Application/DocumentExportComposer.cs`; endpoint `GET /api/v1/projects/{projectId}/documents/export`.
- Migration `0110_document_exports.sql` (faixa reivindicada; a 0088–0089 original foi ocupada).
- `features/delivery/components/tracking-card.tsx` — regra "sem prazo declarado não existe atraso" (`scheduleRisk`).

### Validação em execução
- **Cenário 1 — prazo:** projeto `QA-POSEIDON-2026-07-30-01` criado com `targetDeadline: 2026-09-15`. **Persistido e devolvido pela API** ✓; tela exibe "Começou em"/"Prazo" ✓.
- **Cenário 2 — ZIP sem documento aprovado:** `HTTP 200`, `application/zip`, 247 bytes, contendo só `manifesto.json` com `documentCount: 0` ✓ (regra correta: só aprovados entram).
- **Cenário 3 — ZIP com documento aprovado (ponta a ponta):** após aprovar o documento de QA, `HTTP 200`, 730 bytes:
  ```
  manifesto.json
  sem-etapa/qa-poseidon-2026-07-30-proposta-da-loja-de-bolos-v1.md
  ```
  Manifesto com `documentId`, `version`, `contentHash` (SHA-256) e `path` ✓. **Versão no nome do arquivo e hash no manifesto conforme especificado.**

### Testes automatizados
Existentes: `tracking-card.test.tsx` (9 testes). **Criados: 1** — asserção de semântica `dt`/`dd` (ver correções).

### Problemas encontrados
1. **DEF-01 (alta) — acessibilidade séria, medida no Host real:** rota `/delivery` reprovava com `definition-list: 8` e `dlitem: 48` (impacto *serious*, WCAG 2.1 AA). **Causa raiz:** `tracking-card.tsx` aninhava **dois** níveis de `<div>` entre `<dl>` e `<dt>/<dd>`; a semântica admite **um**. Com dois, os rótulos ficam órfãos e um leitor de tela não associa "Começou em" ao valor. A aritmética confirma a causa: 8 entregas × 3 pares × 2 nós = 48. Complementarmente, `delivery-overview.tsx` usava `<div>` como container de `<Stat>` (que emite `dt`/`dd`) em dois pontos.

### Correções realizadas
- `frontend/src/features/delivery/components/tracking-card.tsx` — lista reestruturada para `dl > div > dt/dd`, com o ícone como primeira coluna de um grid (`grid-cols-[auto_1fr]` + `row-span-2`), preservando o layout visual. `sm:grid-cols-3` → `md:grid-cols-3` (ver DEF-08).
- `frontend/src/features/delivery/components/delivery-overview.tsx` — os dois containers de `<Stat>` passaram de `<div>` para `<dl>`.
- `frontend/src/features/delivery/__tests__/tracking-card.test.tsx` — **teste novo** que percorre todo `dt`/`dd` e exige `<dl>` como pai ou avô. Como nenhum gate canônico roda Playwright, a regressão passa a ser barrada pelo vitest, que **é** cobrado.
- **Justificativa arquitetural:** a correção é local, preserva a aparência e ataca a causa (aninhamento), não o sintoma; o teste foi colocado na camada que o gate realmente executa.
- **Verificação pós-correção (Host reconstruído e reiniciado):** `/delivery` passou de `axe[definition-list:8, dlitem:48]` para **`ok`**; `tracking-card.test.tsx` 10/10 verde.

### Status final
**APROVADA** (com o defeito encontrado corrigido e coberto)

### Pendências
Nenhuma.

---

## Fase 06 — Projetos: criação por modo + bindings auditados

### Requisitos previstos
Formulário Negócio mínimo (Título, Objetivo, Prazo, Logo); **Sigla auto-gerada**; **criticidade estimada pela Bruna**; repositório local automático; workflow recomendado automático; **tecnologias inferidas**; auditoria de binding ponta a ponta; cards com logo e sem caminho de repositório.

### Evidências encontradas no código
- `Modules.Projects/Domain/Project.cs:80` — `Choice(criticality ?? "medium", …)`.
- `Harness.Host/Workers/ChiefContextComposer.cs:104-116` — injeta `Id, Name, Description, Criticality, TargetDeadline, Brand{LogoUrl,PrimaryColor,SecondaryColor,Typography}, Technologies` no contexto da Bruna.
- `tests/Harness.IntegrationTests/Projects/ProjectBindingAuditTests.cs` — auditoria de binding.
- `features/projects/components/project-form.tsx:253` — tablist renderizado **apenas** fora do modo Negócio; `:299` e `:428` — ramo de negócio.
- Migrations `0092_project_business_fields.sql`, `0093_project_audit_bindings.sql`.

### Validação em execução
Criação real via `POST /api/v1/projects` com apenas Título + Objetivo + Prazo (`HTTP 201`):

| Campo | Resultado | Veredito |
|---|---|---|
| `key` (Sigla) | `QA-POSEIDON-2026-07-30-01-LOJA` — derivada do título | ✓ |
| `repositoryUrl` | `~/.harness-poseidon/repositories/…/qa-poseidon-…` criado sozinho | ✓ |
| `targetDeadline` | `2026-09-15T00:00:00+00:00` persistido | ✓ |
| `chiefAgentId` | atribuído automaticamente | ✓ |
| `criticality` | **`medium`** | ✗ |
| `technologies` | **`[]`** | ✗ |

Teste de controle: segundo projeto (`QA-POSEIDON-…-02`) com objetivo explicitamente crítico — *"Sistema crítico de pagamento com cartão de crédito, dados bancários… Usar React no site e API em Python com Postgres"* — retornou **exatamente os mesmos valores**: `criticality: medium`, `technologies: []`.

### Testes automatizados
Existentes: `ProjectBindingAuditTests`. Criados: nenhum (ver justificativa abaixo).

### Problemas encontrados
1. **DEF-06 (média):** não existe **nenhum** estimador de criticidade nem inferência de tecnologias no código — busca por `EstimateCriticality|InferTechnolog|CriticalityEstimat|TechnologyInference` retorna vazio em todo o repositório, e nem o fluxo do Chefe (`Harness.Host/Agents/`, `Modules.Coordination/`) menciona criticidade. O valor é o default de `Project.cs:80`.
   **Efeito real:** a D12 tira do dono a tarefa de estimar criticidade — mas ninguém a assumiu. Como a criticidade alimenta risco e profundidade de revisão (F14/F15), **todo projeto entra no fluxo como risco médio**, inclusive um de pagamentos. O binding funciona (o campo chega à Bruna), mas transporta um default, não uma estimativa.

### Correções realizadas
Nenhuma aplicada, **deliberadamente**: a D12 especifica que quem estima é a **Bruna, no primeiro plano** (juízo de modelo em contexto), não uma heurística determinística. Implementar uma tabela de palavras-chave entregaria um número plausível com aparência de estimativa — exatamente o "inventar" que o invariante de honestidade proíbe, e mudaria regra de negócio sem respaldo no documento. Registrado como pendência com recomendação.

### Status final
**PARCIALMENTE APROVADA**

### Pendências
- Ligar a estimativa de criticidade ao primeiro plano da Bruna (o campo já é injetado no contexto: falta o passo que o **escreve de volta**), e idem para tecnologias inferidas — ou remover ambos do formulário/contrato, conforme a própria regra da fase ("campo sem consumo comprovado é removido ou ligado").

---

## Fase 07 — Quadro e Conversas

### Requisitos previstos
Filtros do Quadro reduzidos a 5; etiquetas no léxico ("Tarefa", sem "tarefa de agente"); Conversas em grade compacta com busca; consumir `useActiveProject()`; **nenhuma UI de criação de card**.

### Validação em execução
| Requisito | Resultado |
|---|---|
| Quadro sem "tarefa de agente" | ✓ |
| Filtros reduzidos | ✓ (6 combos, incluindo o seletor global do shell) |
| Conversas com busca | ✓ (2 campos) |
| Sem UI de criação de card | ✓ (nenhum botão de criação exposto) |

### Testes automatizados
Existentes: `board.test.tsx`, testes de conversas. Criados: nenhum.

### Problemas encontrados
Nenhum.

### Status final
**APROVADA**

### Pendências
Nenhuma.

---

## Fase 08 — Equipe (humanização) + Executar Projeto de negócio

### Requisitos previstos
Card da Bruna e perfis em linguagem de negócio; remover saúde/heartbeat/lease/diagnóstico do Negócio; ações renomeadas; perfil de pessoa com competências; **Executar Projeto = botão "Abrir <projeto>"** expondo só URL do front; manifesto com marcação de serviço voltado ao usuário.

### Evidências encontradas no código
- `features/run-project/components/business-run-panel.tsx` — painel de negócio consumindo `userFacing`.
- Migration `0111_run_target_user_facing.sql` (marcação no manifesto de run targets).

### Validação em execução
| Requisito | Resultado |
|---|---|
| Identidade fixa da Bruna | ✓ ("Bruna Magalhães") |
| Sem heartbeat/lease/fencing/diagnóstico no Negócio | ✓ |
| Ações renomeadas (Pausar trabalhos / Transferir liderança) | ✓ |
| Botão "Abrir \<projeto\>" | ✓ ("Abrir Poseidon") |
| Sem lista técnica de serviços no Negócio | ✓ (sem vite/npm/dotnet/porta) |

### Testes automatizados
Existentes: Playwright dos dois modos + teste do launcher. Criados: nenhum.

### Problemas encontrados
Nenhum defeito. Limite conhecido e declarado pelo autor da fase (mantido como achado): a marcação "voltado ao usuário" só reconhece pacotes Node (framework declarado ou `index.html` servido); Dockerfile, compose, Maven, Gradle e Python nunca marcam.

### Status final
**APROVADA**

### Pendências
- Estender a detecção de serviço voltado ao usuário a manifestos não-Node, se o produto precisar rodar projetos nessas pilhas.

---

## Fase 09 — Documentos unificados + aprovação no fluxo

### Requisitos previstos
Tela única "Documentos" com aba "Aguardando sua aprovação" e ação de aprovar ali; Aprovações → redirect; Governança e Documentos de Governança movidos ao Técnico; **aprovar reflete no gate da etapa**.

### Evidências encontradas no código
- `Modules.Documents/Domain/Documents/DocumentAggregate.cs:194-203` — `RequestApproval` leva o documento a `AwaitingApproval` atomicamente com a solicitação; `:205` — `ResolveApproval`.
- `Harness.Host/Documents/DocumentEndpoints.cs` — `POST /documents`, `/{id}/transitions`, `/{id}/versions`, `/approvals`, `/approvals/{id}/resolution`.

### Validação em execução — fluxo ponta a ponta real
| Passo | Chamada | Resultado |
|---|---|---|
| 1 | `POST /api/v1/documents` (kind `prd`) | `201`, `state: inElaboration` |
| 2 | `POST /documents/{id}/transitions` → `inReview` | `200`, `state: inReview` |
| 3 | `POST /api/v1/approvals` (documentId) | `201`, `state: pending` — **documento passa a `awaitingApproval`** |
| 4 | `POST /approvals/{id}/resolution` `{decision: approved}` | `200`, aprovação `approved` |
| 5 | `GET /documents/{id}` | **`state: approved`** ✓ |
| 6 | `GET /projects/{id}/documents/export` | documento aprovado presente no ZIP ✓ |

O "degrau humano" descrito no board **funciona em execução real**: a aprovação do dono conclui o ciclo e o artefato passa a contar para a exportação.

Redirect validado: `/approvals` → `/documents?tab=approvals` ✓. Aba "Aguardando sua aprovação" presente ✓.

### Testes automatizados
Existentes: cobertura de aprovação e gate. Criados: nenhum.

### Problemas encontrados
1. **DEF-09 (baixa) — imprecisão de contrato:** `POST /documents/{id}/transitions` com `toState=awaitingApproval` a partir de `in_review` responde **`200` devolvendo o recurso inalterado** (`DocumentEndpoints.cs:253-258`). É um no-op **deliberado e comentado** (o frontend envia a intenção antes do `POST /approvals`), mas a mesma API responde `409 InvalidState` para uma transição inválida análoga (`approved`). Um cliente não tem como distinguir "transicionei" de "ignorei".
2. **DEF-09b (baixa):** `kind` aceita apenas minúsculas (`prd`, `spec`, `design`, `runbook`, `note`, `report` — `IDocumentStore.cs:128`), enquanto o enum de contrato é PascalCase (`Prd`, `Spec`…) e o OpenAPI declara `kind` como string livre. O erro (`"Value is outside the supported catalog. (Parameter 'command')"`) não informa os valores aceitos nem o campo real.

### Correções realizadas
Nenhuma aplicada: mudar o retorno do no-op quebraria o frontend que depende dele — é decisão de contrato do dono do módulo, não correção de auditoria.

### Status final
**APROVADA**

### Pendências
- Tornar o no-op explícito (`204`, ou `200` com campo indicando que nada mudou) e alinhar frontend.
- Publicar o enum de `kind` no OpenAPI e citar os valores válidos na mensagem de erro.

---

## Fase 10 — Etapa de Prototipação + três caminhos de entrada

### Requisitos previstos
Etapa opcional de Prototipação com gate "Identidade visual e protótipo aprovados **ou herdados**"; três caminhos de entrada (prosa, documento de requisitos, ZIP React); a Bruna registra o caminho em uso e o que herdou.

### Evidências encontradas no código
- `Modules.Coordination/Application/PrototypingStagePolicy.cs` (consumida por `Host/Workflows/CanonicalWorkflowTemplates.cs` e `Host/Prototyping/PrototypingStageEndpoints.cs`) e `PrototypingIntakePolicy.cs` (consumida pelos endpoints) — **ambas ligadas ao fluxo de produção**.
- `Modules.Prototyping`, `MultimodalIntakeService` (registrado em `HostApplication.cs`).

### Validação em execução
`GET /api/v1/projects/{id}/prototyping-stage` no projeto de QA recém-criado → `HTTP 200`:
```json
{"state":"Pending","blocksAdvance":true,
 "reasonCode":"prototyping.gate_pending_prototype_approval",
 "businessMessage":"Preparei o protótipo das telas e preciso do seu aval antes de desenvolver.",
 "entryPath":"Prose","inherited":["marca da organização"],
 "questions":["Quais telas o projeto precisa ter?"],
 "prototypingMode":"autonomousGeneration"}
```
Três exigências verificadas de uma vez: **gate bloqueia** (`blocksAdvance: true`), **caminho de entrada registrado** (`entryPath: Prose`), **herança declarada** (`inherited: ["marca da organização"]`) — e a mensagem chega **em linguagem de negócio**, sem código cru.

### Testes automatizados
Existentes: `e2e/prototyping-stage.spec.ts` + testes dos três caminhos. Criados: nenhum.

### Problemas encontrados
Nenhum defeito funcional. Observação: a faixa de migrations 0094–0096 não foi usada — a fase foi entregue sobre estruturas existentes, o que é legítimo.

### Status final
**APROVADA**

### Pendências
Nenhuma.

---

## Fase 11 — Auditoria e simplificação das telas de administração

### Requisitos previstos
Corrigir layout de Fluxos de trabalho; assistente de 3 passos em Canais; verificar Provedores; verificar Organizações; **provar que desabilitar Ferramenta restringe agentes**; Licenças simplificada; Onboarding fora do menu; varrer cada tela contra o léxico.

### Evidências encontradas no código
- `Modules.Tools/Domain/ToolExecutionPolicy.cs:38` — `ToolPolicyDecision.Deny("tool_disabled", …)`.
- Testes: `tests/Harness.UnitTests/Tools/ToolExecutionPolicyTests.cs` e **`tests/Harness.IntegrationTests/Agents/AgentRunHappyPathTests.cs`** — a recusa é exercida no caminho de execução de agente, não só em unidade.
- Isenção `channels.cli.*` no gate de vocabulário, com motivo (D13: curl/token só no Técnico).

### Validação em execução
Todas as telas de administração carregadas no Host real (`/workflows`, `/channels`, `/organizations`, `/licenses`, `/notifications`, `/settings`, `/prototypes`): **sem violação axe bloqueante, sem erro de console, sem HTTP ≥ 400, sem vazamento de léxico e sem overflow horizontal**.

### Testes automatizados
Existentes: `e2e/f11-admin-audit.spec.ts` (passa), menu/paleta 28/28. Criados: nenhum.

### Problemas encontrados
Nenhum. A exigência mais difícil da fase — "habilitar/desabilitar **de fato** restringe agentes" — tem prova funcional em integração, não apenas em unidade.

### Status final
**APROVADA**

### Pendências
Nenhuma.

---

## Fase 12 — Backend: disciplina de coordenação + guardas de laço (B4+B9)

### Requisitos previstos
(1) política de espera (timeout=checkpoint, heartbeat=vivo≠concluído, proibido matar agente vivo); (2) **circuit breaker por card** (3 falhas abrem, só replanejamento reabre); (3) profundidade máxima 3–4 com reagrupamento em ondas; (4) **guardas de laço** (teto de turnos, reentrância, taxa por janela, detecção de ciclo causal); (5) amostragem de cauda.

### Evidências encontradas no código e wiring medido

| Item | Artefato | Consumidor de produção | Veredito |
|---|---|---|---|
| (2) Circuit breaker | `CardCircuitBreakerPolicy` | `Host/Agents/CardCircuitBreakerService.cs` + `ChiefBacklogLoopService` + `HostApplication` | **LIGADO** ✓ |
| (2) Persistência | `0097_card_circuit_breakers.sql` | `SqliteCardCircuitBreakerStore` + `PostgresCardCircuitBreakerStore` | **LIGADO** ✓ |
| (5) Amostragem de cauda | `TurnTailSamplingPolicy` | `Host/Observability/TurnTailSamplingProcessor.cs` | **LIGADO** ✓ |
| (1) Política de espera | `AgentWaitPolicy` (155 linhas) | **nenhum** | **ÓRFÃO** ✗ |
| (4) Guardas de laço | `ChiefLoopGuardPolicy` (177 linhas) | **nenhum** | **ÓRFÃO** ✗ |
| (4) Ciclo causal | `CausalCycleDetector` | só `ChiefLoopGuardPolicy` (que também é órfã) | **ÓRFÃO** ✗ |
| (3) Profundidade | `PlanDepthPolicy` (97 linhas) | **nenhum** | **ÓRFÃO** ✗ |
| (4) Persistência | `0098_chief_loop_guards.sql` | **nenhum store lê ou escreve a tabela** | **ÓRFÃO** ✗ |

### Validação em execução
O circuit breaker tem serviço registrado e tabela com store nos dois bancos — caminho de produção completo. As demais políticas são classes **estáticas puras** (`public static class X { Evaluate(…) }`): não há DI que pudesse escondê-las, e a busca por chamadas em `src/` (excluindo o próprio arquivo, `bin/` e `wwwroot`) retorna zero. `AgentWaitPolicy` tem 2 arquivos de teste e `ChiefLoopGuardPolicy` 36 referências de teste — **testadas, nunca executadas**.

### Testes automatizados
Existentes: `ChiefLoopGuardPolicyTests`, `AgentWaitPolicyTests` e afins — todos verdes, todos exercitando a política **fora** do fluxo real. Criados: nenhum (a lacuna aqui não é de teste, é de fiação).

### Problemas encontrados
1. **DEF-04 (média):** a disciplina de espera e as guardas de laço **não protegem nada em execução**. O sistema hoje não tem teto de turnos autodisparados, nem reentrância por `demand_plan`, nem detecção de ciclo causal em runtime. A migration `0098` cria uma tabela que ninguém usa — o que é pior que não existir, porque sugere cobertura inexistente.
2. Atenuante factual registrado pela própria campanha: a análise dos seis pontos de enqueue concluiu que não há caminho de turno **autodisparado** hoje (todos nascem de mensagem do usuário), o que reduz a probabilidade de laço real no presente — mas a guarda pedida pela fase continua ausente.

### Correções realizadas
Nenhuma. Ligar essas políticas exige decidir **onde** o ponto de chamada entra no ciclo de turno do Chefe e qual store persiste o estado — mudança de fluxo de produção com risco de laço/deadlock, fora do que uma auditoria deve aplicar sem homologação.

### Status final
**PARCIALMENTE APROVADA**

### Pendências
- Ligar `AgentWaitPolicy` ao ponto de espera do executor.
- Ligar `ChiefLoopGuardPolicy` + `CausalCycleDetector` ao ciclo de turno, com store para `chief_loop_guards` (ou remover a migration).
- Ligar `PlanDepthPolicy` ao planner (reagrupamento em ondas).

---

## Fase 13 — Backend: protocolo de ciclo de vida + autoridade por identidade (B5)

### Requisitos previstos
(1) **7 tipos de mensagem** (`status`, `dispatch`, `worker_done`, `escalation`, `decision_gate`, `heartbeat`, `merge_ready`); (2) conclusão exatamente uma vez, inclusive em falha; (3) **autoridade por `task_id`+`attempt_id`+`fencing_token`**, aceitando agente reiniciado e rejeitando terceiro; (4) honestidade de proveniência.

### Evidências encontradas no código e wiring medido

| Item | Situação medida |
|---|---|
| (3) Autoridade por fencing | **LIGADA E FUNCIONANDO** — `Harness.Persistence.Abstractions/RunnerIpc/RunnerMessageTransition.cs:34-42` rejeita com `RunnerMessageRejection.StaleFencingToken` quando o token da mensagem é menor que o registrado, e **aceita a mesma tentativa vinda de outro processo** (exatamente o caso "agente reiniciado"). Persistência: `0099_runner_attempt_fencing.sql` + stores Sqlite/Postgres. |
| (1) Tipos de mensagem | **NÃO ENTREGUE em execução** — o runtime usa `SharedKernel/RunnerIpc/RunnerMessageTypes.cs`, que declara **3** tipos: `heartbeat`, `checkpoint`, `completion`. |
| (1) Versão completa | `DispatchLifecycleTypes` (em `Coordination/Application/DispatchLifecycle.cs`) declara os 7 tipos + `LegacyCompletion` — e está **órfã**, com **0 referências de produção e 0 de teste**. |
| (3) Política espelhada | `DispatchAuthorityPolicy` aparece **apenas dentro de um comentário XML** (`RunnerMessageEnvelope.cs:11`), nunca é chamada. |
| (2) Conclusão única | `TurnCompletionPolicy` — **órfã**. Detectada pelo gate novo, não pela inspeção manual. A garantia de "conclusão exatamente uma vez, inclusive em falha" **não passa por esta política em execução**; o que existe é a idempotência do store (`IdempotentReplay`), que cobre o caso mas não é o que a fase entregou. |
| (4) Proveniência | `WorkProvenance`, `ActiveDispatch`, `ClaimedIdentity`, `DispatchAuthorityDecision` — todos órfãos. |
| Migration 0100 | Não existe (faixa 0099–0100 usada pela metade). |

### Validação em execução
A regra de autoridade é exercida no store durável, com testes de recuperação/concorrência verdes no `verify.sh`. Já `escalation` (distinto de falha), `decision_gate` (distinto de `agent_requests`) e `merge_ready` **não existem como tipo aceito pelo runtime**: uma mensagem desses tipos não seria reconhecida por `RunnerMessageTypes.All`.

### Testes automatizados
Existentes: cobertura de fencing em `RecoveryTests`/`ConcurrencyTests` (verde). Criados: nenhum.

### Problemas encontrados
1. **DEF-07 (baixa/média):** existem **duas** definições concorrentes do protocolo — a que roda (3 tipos) e a que a fase entregou (7 tipos, órfã). O produto não distingue escalação de falha nem decisão de plano de pergunta de agente, que era o ponto central da tarefa (1). A duplicação é dívida arquitetural: quem ler `DispatchLifecycle.cs` conclui, erradamente, que o protocolo está completo.

### Correções realizadas
Nenhuma. Trocar o conjunto de tipos aceitos pelo runtime altera o contrato IPC com runners em voo — mudança de protocolo, não correção de auditoria.

### Status final
**PARCIALMENTE APROVADA** — o item mais crítico da fase (autoridade por fencing, o que protege trabalho real contra rejeição indevida) está entregue e funcionando; o protocolo de mensagens não.

### Pendências
- Unificar as duas definições: promover `DispatchLifecycleTypes` a fonte única e migrar `RunnerMessageTypes`, ou remover a órfã.
- Ligar `WorkProvenance` ao registro de proveniência.

---

## Fase 14 — Backend: esforço, fronteiras negativas e verificação em camadas (B3+B11+B2)

### Requisitos previstos
(1) `EffortPolicy` determinística + **regra de fan-out**; (2) **campo de fronteira negativa no card**, preenchido pelo planner e injetado no bundle; (3) **verificação em três camadas** (determinística → comportamento × critérios → intenção × objetivo), sem compensação entre camadas; (4) **assimetria de modelo** como regra de roteamento.

### Evidências encontradas no código e wiring medido

| Método | Consumidor de produção | Veredito |
|---|---|---|
| `CodeDiagnosticsGate.Inspect` / `.ApplyTo` | `ChiefBacklogLoopService.cs:417,419,965,967` | **LIGADO** ✓ |
| `LayeredVerificationPolicy.MayOccupyReviewer` | `ChiefBacklogLoopService.cs:425,973` | **LIGADO** ✓ (camada i) |
| `EffortPolicy.Decide` | via `BlastRadiusPolicy.Assess:80` → `ChiefBacklogLoopService:476` | **LIGADO** (indireto) ✓ |
| `LayeredVerificationPolicy.Evaluate` | **nenhum** | **ÓRFÃO** ✗ |
| `EffortPolicy.SelectFanOut` | **nenhum** | **ÓRFÃO** ✗ |
| `EffortPolicy.RouteModel` | **nenhum** | **ÓRFÃO** ✗ |
| `NegativeBoundaryPolicy` (157 linhas) | **nenhum** | **ÓRFÃO** ✗ |
| Migrations 0101–0102 | não existem | ✗ |

### Validação em execução
A camada determinística funciona de verdade: o loop do Chefe inspeciona diagnósticos de compilação e **só ocupa revisor humano/modelo se a camada (i) passar** — que é precisamente a economia pretendida pela fase. O que não roda: o veredito das três camadas com severidade (`Evaluate`), a escolha de fan-out, o roteamento por assimetria de modelo e a fronteira negativa no card.

### Testes automatizados
Existentes: testes de política verdes (22 referências para `NegativeBoundaryPolicy`, todas em teste). Criados: nenhum.

### Problemas encontrados
1. **DEF-05 (média):** a fronteira negativa — o campo que diz ao agente "não toque em X, é da tarefa Y" — **não é preenchida pelo planner nem injetada no bundle**, apesar de a política existir e estar testada. Em execução, agentes irmãos continuam sem essa proteção declarativa. Sem migration (0101–0102 vazias), também não há onde persistir o campo.
2. As camadas (ii) e (iii) da verificação existem em `Evaluate`, mas o gate real usa só a camada (i).

### Correções realizadas
Nenhuma. Injetar fronteira negativa no bundle e ativar as camadas superiores muda o que é enviado ao agente e o critério de aprovação de card — alteração de fluxo produtivo que exige decisão do dono.

### Status final
**PARCIALMENTE APROVADA**

### Pendências
- Preencher e persistir a fronteira negativa (migrations 0101–0102 estão livres).
- Ligar `LayeredVerificationPolicy.Evaluate` ao Review Gate; ligar `SelectFanOut` ao planner e `RouteModel` ao roteamento.

---

## Fase 15 — Backend: grafo de código Roslyn + raio de impacto (B6)

### Requisitos previstos
(1) `ICodeGraphIndex` derivado e reconstruível (Roslyn para C#, servidor TS para o frontend), armazenado por projeto; (2) consumidores: validação plano×grafo, **raio de impacto** alimentando `risk_tier` e profundidade de revisão, camada (i) da F14; (3) alimentar o self-map de Architecture.

### Evidências encontradas no código e wiring medido

| Item | Situação |
|---|---|
| `ICodeGraphIndex` / `RoslynCodeGraphIndex` | `Harness.SharedKernel/CodeGraph/` + `Host/Architecture/` ✓ |
| `CodeGraphDerivationService` | registrado em `HostApplication.cs` **e injetado no `ChiefBacklogLoopService` (`:45`)** ✓ |
| `PlanGraphValidationPolicy.Validate` | chamado em `ChiefBacklogLoopService:651` ✓ |
| `BlastRadiusPolicy.Assess` (raio de impacto → risco) | chamado em `ChiefBacklogLoopService:476` ✓ |
| Persistência | `0103_code_graph.sql` + `SqliteCodeGraphStore` + `PostgresCodeGraphStore` ✓ |
| Pacotes Roslyn | único acréscimo de dependência autorizado ✓ |
| **Servidor TS (frontend)** | **não existe** — busca por `TypeScriptCodeGraph|tsserver|ts-morph` retorna vazio ✗ |

### Validação em execução
Diferentemente do que o board registrava em meio-campanha ("políticas prontas, ponto de chamada não existe"), o commit `69f8d17e` **ligou os gates ao fluxo de produção**: hoje o loop do Chefe deriva o grafo, valida o plano contra ele e recalcula risco por raio de impacto. Verificado por leitura das chamadas, não por nota de quadro.

### Testes automatizados
Existentes: índice reconstruível sobre fixtures com Roslyn real (semântica, digest determinístico, erro de sintaxe). Criados: nenhum.

### Problemas encontrados
1. **Segunda entrega declarada e não feita:** sem índice de TypeScript, todo card de frontend é classificado como **não medido** (`UnindexedPaths`/`UnverifiableCardIds`) e nunca entra em `ValidatedCardIds`. O comportamento é honesto por construção ("não medido ≠ zero"), mas a cobertura do frontend simplesmente não existe.
2. **Custo não medido:** a derivação monta compilação em memória com todas as fontes + ~200 referências; não há medição de tempo/memória para o repositório inteiro, e o próprio autor registrou que isso não pode entrar em caminho de requisição sem medição.

### Correções realizadas
Nenhuma (a fronteira da própria fase autoriza dividir a entrega do TS).

### Status final
**PARCIALMENTE APROVADA**

### Pendências
- Entregar o índice TypeScript do frontend.
- Medir custo de indexação do repositório completo antes de expor a derivação em caminho de requisição.

---

## Fase 16 — Backend: avaliação MAST + pass@k + promoção de aprendizado (B1+B12+B10)

### Requisitos previstos
(1) **Taxonomia de 14 modos / 3 categorias persistida** e classificada por avaliador em contexto fresco no fim de cada tentativa; dimensão nos spans; **painel de distribuição** no Técnico; a Bruna consome a distribuição ao decidir decomposição/especialista/revisão. (2) **pass@k** por par agente+modelo+tipo de card, combinado a `model_invocations` no painel de produtividade. (3) Ciclo de aprendizado com promoção **sob aprovação humana**.

### Evidências encontradas no código e wiring medido

| Item | Artefato | Consumidor de produção | Persistência | Painel |
|---|---|---|---|---|
| (1) MAST | `MastTaxonomy.cs` (198 linhas), `MastCategory`, `MastFailureMode`, `MastDistribution`, `MastAdvice` | **nenhum** | **nenhuma migration** (faixa 0105–0107 vazia) | **nenhum** |
| (2) pass@k | `PassAtKPolicy.cs` (122 linhas), `CapabilityPair`, `PassAtKMeasurement` | **nenhum** | nenhuma | **nenhum** |
| (3) Promoção | `LearningPromotionPolicy.cs` (160 linhas), `LearningCandidate`, `LearningStage` | **nenhum** | — | — |

Buscas de confirmação: `mast` em migrations → vazio; `mast` em `frontend/src/**/*.tsx` → vazio; `passAtK|pass@k` no frontend → vazio.

### Validação em execução
**Não há o que executar.** As três políticas são classes estáticas sem nenhuma chamada de produção; nenhuma tentativa de agente é classificada ao encerrar; nenhuma distribuição é calculada, persistida ou exibida; nenhum `pass@k` é medido. O `AgentRunOutcomeClassifier` pré-existente (com 4 consumidores) continua fazendo a classificação **antiga** — a taxonomia de 14 modos não entrou no fluxo.

Ressalva justa: o ciclo de `learning_candidates` (endpoints `promotion`, `review`, `evaluations`, `rollback`, `shadow` em `governance-runtime`) **existe e é anterior à campanha** — a fronteira da fase mandava não alterá-lo além da promoção. A aprovação humana permanece obrigatória por esse caminho pré-existente; o que a fase entregou (`LearningPromotionPolicy`) não é o que roda.

### Testes automatizados
Existentes: 25 referências de teste para `MastTaxonomy`, 15 para `PassAtKPolicy` — **todas verdes, nenhuma executando código de produção**. Criados: nenhum (a lacuna é de fiação, não de teste).

### Problemas encontrados
1. **DEF-02 (alta):** a fase foi declarada concluída no quadro, mas nenhuma de suas três entregas funciona em execução real. É o caso mais claro de "código morto com aparência de pronto": os gates da fase passaram porque testam a política isolada.

### Correções realizadas
Nenhuma. Entregar a F16 é implementá-la (classificador no encerramento de tentativa, migrations 0105–0107, cálculo de pass@k, painel Técnico e consumo pela Bruna) — trabalho de fase, não de auditoria, e sem respaldo para eu decidir sozinho onde cada gancho entra.

### Status final
**REPROVADA**

### Pendências
- Classificar cada tentativa no encerramento com a taxonomia de 14 modos e persistir (0105–0107).
- Adicionar dimensão MAST aos spans e painel de distribuição no modo Técnico.
- Calcular pass@k por par agente+modelo+tipo e exibir junto a `model_invocations`.
- Ligar `LearningPromotionPolicy` ao ciclo de promoção existente (mantendo a aprovação humana).

---

## Fase 17 — Backend: revisor contínuo + hooks por worktree (B7+B8)

### Requisitos previstos
(1) **Revisor contínuo**: modelo pareado lê cada rodada do executor e injeta observação (aparte/preocupação/bloqueio); obrigatório em risco alto/crítico; conta distinta do executor. (2) **Hooks por worktree**: a Bruna gera configuração de hooks dentro da worktree da tentativa, aplicada pelo CLI em tempo de edição, com perfis de rigor. (3) **Lint de definição de persona** (Default-FAIL na criação/alteração).

### Evidências encontradas no código e wiring medido

| Item | Artefato | Consumidor de produção |
|---|---|---|
| (1) Revisor contínuo | `ContinuousReviewPolicy.cs` (119 linhas), `ReviewRemark`, `ContinuousReviewDecision` | **nenhum** |
| (2) Hooks por worktree | `WorktreeHookPolicy.cs` (156 linhas), `GeneratedHook`, `WorktreeHookPlan`, `HookRigor` | **nenhum** |
| (3) Lint de persona | `PersonaDefinitionLint.cs` (129 linhas), `PersonaDefinition`, `PersonaLintResult` | **nenhum** |
| Migrations 0108–0109 | não existem | — |

Buscas de confirmação: `ContinuousReview|pairedReview` em `Harness.Runner` e `Modules.Execution` → vazio; geração de arquivo de hooks em worktree → vazio.

### Validação em execução
**Não há o que executar.** Nenhuma rodada de executor é lida por revisor pareado; nenhuma configuração de hooks é gerada na worktree da tentativa; a criação de persona **não** passa por lint (o `PersonaEligibilityPolicy` pré-existente, que roda em `ChiefBacklogLoopService:160`, trata de elegibilidade para executar — não da combinação perigosa de ferramentas, escopo largo e ausência de denylist que a F17 pedia).

### Testes automatizados
Existentes: 14 referências para `ContinuousReviewPolicy`, 21 para `PersonaDefinitionLint` — verdes e isoladas. Criados: nenhum.

### Problemas encontrados
1. **DEF-03 (alta):** as três entregas da fase existem apenas como políticas puras. O gate declarado da fase ("rodada com bloqueio do revisor interrompe antes da submissão; hook gerado corresponde ao card; persona perigosa recusada") **não pode ser satisfeito em execução real** — não há caminho que chegue a essas políticas.

### Correções realizadas
Nenhuma, pelas mesmas razões da F16.

### Status final
**REPROVADA**

### Pendências
- Ligar o revisor contínuo ao ciclo do executor CLI, com conta distinta.
- Gerar a configuração de hooks na worktree da tentativa e aplicá-la.
- Tornar o lint de persona Default-FAIL na criação/alteração.

---

## Regressão e gates

### Gate canônico (`tools/backend/verify.sh`)
Executado com o Host parado (o `LauncherSmokeTests` disputa a porta 5173 — armadilha conhecida e registrada).

**Primeira execução — com o daemon do Docker fora do ar:**

| Suíte | Resultado |
|---|---|
| Build .NET | **0 erros** ✓ |
| `Harness.UnitTests` | **1305/1305** ✓ |
| `Harness.ArchitectureTests` | **20/20** ✓ |
| `Harness.ContractTests` | **42/42** ✓ |
| `Harness.ConcurrencyTests` | **8/8** ✓ |
| `Harness.RecoveryTests` | 11/12 — **1 falha** |
| `Harness.IntegrationTests` | 224/233 — **9 falhas** |
| Frontend `npm run check` | lint 0 erros, typecheck ✓, `gate:vocabulary` sem violações, **vitest 98 arquivos / 766 testes** ✓ |

**As 10 falhas têm causa única e provada: o daemon do Docker não estava em execução.**

```
System.InvalidOperationException : Docker could not list managed Docker resources (exit 1):
failed to connect to the docker API at unix:///Users/mateus/.docker/run/docker.sock
```

São 4 testes de Docker (`DockerSandboxProviderPocTests`, `DockerIsolatedAttemptCompositionTests`, `DockerIsolatedCodexAgentExecutorTests`, `RunTargetDockerLifecycleTests`) e 6 de Postgres (`PostgresMultiuserLoadTests`, `PostgresServerModeHostTests`, `PostgresSkipLockedPocTests`, `PostgresTenantRowLevelSecurityTests`, `SqliteToPostgresMigrationTests`, `ProductionDurableExecutionRecoveryTests.PostgresSurvivesSigkill…`) — os de Postgres também sobem container gerenciado via Docker.

**Nenhuma tem relação com as alterações desta auditoria**, que eram, naquele momento, exclusivamente de frontend (dois componentes React e um teste vitest) e não são carregadas por nenhuma dessas suítes.

**Segunda execução — regressão completa final, com Docker ativo e todas as correções aplicadas:**

| Etapa | Resultado |
|---|---|
| `scan-secrets` (modo full) | sem segredos ✓ |
| `governance-lint` | **0 erros, 0 warnings** ✓ |
| Build .NET | 0 erros ✓ |
| `Harness.UnitTests` | **1305/1305** ✓ |
| `Harness.ArchitectureTests` | **22/22** ✓ (20 + 2 criados nesta auditoria) |
| `Harness.ContractTests` | **42/42** ✓ |
| `Harness.ConcurrencyTests` | **8/8** ✓ |
| `Harness.RecoveryTests` | **12/12** ✓ (era 11/12) |
| `Harness.IntegrationTests` | **233/233** ✓ (era 224/233) |
| Frontend `npm run check` | **766/766** ✓ |
| **`verify.sh`** | **exit 0 — verde de ponta a ponta** |

Uma observação de processo colhida no caminho: a primeira tentativa desta regressão final **abortou antes de rodar qualquer teste**, com `governance-lint: errors=1 / GOV019` — porque o próprio `RELATORIO-VALIDACAO-POSEIDON.md` é um Markdown novo na raiz e o repositório exige que todo `.md` esteja declarado. O gate estava certo. A solução foi o mecanismo que ele mesmo prevê: registrar o relatório em `markdownAllowlist` (`governance/manifest.yaml`), com `path` e `reason` — declarando que é registro de validação com data fechada, não norma canônica.

### Suíte Playwright (fora de qualquer gate)
- **Mock (MSW, 4173):** 57 passaram, 9 falharam no viewport desktop (as "18" citadas no quadro = 9 × 2 viewports).
- **Classificação individual das 9 falhas** — feita por inspeção do produto, não por aceitação da nota do quadro:

| Spec | Expectativa | Veredito |
|---|---|---|
| `chat-business-actions` (×2) | `getByText('Equipe virtual')` | **Teste obsoleto** — a D15/F2 mandou eliminar essa identidade; o produto a substituiu por "Bruna Magalhães" |
| `fe1-flow`, `golden-path-ux` (×3) | `getByRole('tab', {name:'Identidade'})` | **Teste obsoleto** — `project-form.tsx:253` só renderiza o tablist fora do modo Negócio; a D12/F6 exige formulário de 4 campos no Negócio |
| `fe2-flows` | `getByRole('heading', {name:'Orquestrador'})` | **Teste obsoleto** — léxico §2 renomeou para "Equipe" |
| `fe3-flows` | `getByText('Frontend Vite (dev)')` | **Teste obsoleto** — D8/F8 removeu a lista técnica do Negócio |
| `golden-path-ux` | `getByText('Binding pendente')` | **Teste obsoleto** — `orchestrator.definitions.*` migrou para o Técnico |
| `visual-identity` | `combobox 'Perfil de trabalho'` | **Teste obsoleto** — F2 substituiu por "Modo de trabalho" (confirmado em execução) |

  Conclusão: as 9 falhas **de fato** são expectativas pré-campanha, como o quadro afirmava — mas isso só pôde ser dito depois de verificar cada uma contra o produto. **A suíte está desatualizada e, enquanto não for atualizada, permanece inútil como gate.**

- **API real (`playwright.real.config.ts` apontado para o Host):** reprovou por acessibilidade em `/delivery` — o defeito DEF-01, corrigido nesta auditoria. Após a correção e o rebuild, a rota passou a `ok`.

### QA de navegador próprio (evidência primária)
`artifacts/qa-poseidon/tools/qa-scan.mjs` — login real + 17 rotas, medindo axe/console/HTTP/léxico/overflow. `qa-fases.mjs` — 24 asserções derivadas literalmente dos requisitos das fases de frontend: **23 passaram**; a única falha (F3) é o rótulo do card de aprovações, analisado acima.

---

## Achados arquiteturais (além da aderência funcional)

1. **Política testada ≠ política ativa.** O padrão dominante da campanha nas fases de backend foi entregar `public static class XPolicy { Evaluate(…) }` com bateria de testes e **sem ponto de chamada**. Como o gate de cada fase é "build + testes verdes", ele não distingue as duas situações.
   **Corrigido nesta auditoria (DEF-00):** `tests/Harness.ArchitectureTests/PolicyWiringTests.cs` reprova o build quando uma classe **estática** em `src/Modules/*/Application/` não tem nenhuma referência de produção. O critério é estreito de propósito — só estáticas, que não podem ser injetadas por DI, o que elimina falso positivo — e a busca **descarta comentários**, senão uma menção em `<see cref="…"/>` mascararia a orfandade (foi exatamente o que aconteceu com `DispatchAuthorityPolicy` na minha primeira medição manual).
   As 14 órfãs atuais entram em `DeclaredDebt`, cada uma com a fase responsável, no mesmo padrão das `EXEMPTIONS` do gate de vocabulário. Um segundo teste (`DeclaredDebtHasNoStaleEntries`) impede que a lista envelheça: quando a política for ligada ou removida, a linha correspondente passa a reprovar. Efeito prático: **o repositório continua verde hoje, novas políticas órfãs passam a ser barradas, e a dívida fica visível no código em vez de num relatório.**
2. **Nenhum gate roda Playwright.** Recomendação: incluir a suíte (após atualizar as 9 expectativas obsoletas) no `verify.sh` ou em CI. Enquanto isso, defeitos de tela dependem de auditoria manual — foi assim que o DEF-01 sobreviveu.
3. **Duplicação de protocolo (F13).** Duas definições concorrentes de tipos de mensagem, uma ativa e incompleta, outra completa e morta.
4. **Migration sem store (F12).** `0098_chief_loop_guards` cria tabela que ninguém usa: sugere capacidade inexistente a quem inspeciona o schema.
5. **DEF-08 — breakpoint fantasma.** `tailwind.config.js` declara só `md/lg/xl`; há **42 ocorrências de `sm:` em 29 arquivos** que não geram CSS. O próprio código já registra a convenção correta (`documents-tabs.tsx:21`: *"O prefixo é `md` de propósito: este tema declara apenas md/lg/xl"*). Corrigi a ocorrência que estava no caminho do DEF-01; as demais **não** foram alteradas de propósito: trocar 41 classes muda o layout de 28 telas entre 768 px e 1024 px, e uma mudança visual dessa largura precisa de homologação do dono, não de decisão de auditor.
6. **Gate de vocabulário mede catálogo, não tela** (ver F1).
7. **Higiene operacional:** `~/.harness-poseidon/logs/poseidon.log` estava com **267 MB** (sem rotação). Não afeta funcionalidade, mas cresce sem limite em uso prolongado.

---

## Dados de QA criados

Convenção `QA-POSEIDON-2026-07-30-*`, todos no banco local:

| Registro | Id | Finalidade |
|---|---|---|
| Projeto `QA-POSEIDON-2026-07-30-01 Loja de bolos da Dona Marta` | `01KYT0NWY6Z7ESN6XHHQ66FJP7` | F6 (4 campos), F5 (prazo/ZIP), F10 (prototipação) |
| Projeto `QA-POSEIDON-2026-07-30-02 Pagamentos com cartao` | — | F6 (controle de criticidade/tecnologias) |
| Documento `QA-POSEIDON-2026-07-30 Proposta da loja de bolos` | `01KYT0TWJCZXXJBV9YEB0EGPPY` | F9 (ciclo completo até `approved`) |
| Aprovação `QA-POSEIDON-2026-07-30 aprovar proposta` | `01KYT0X08WP0VH4RJYEARJNZCQ` | F9 (resolução) |

Nenhum dado pré-existente foi alterado ou removido; nenhuma operação destrutiva foi executada; nenhum segredo aparece neste relatório ou nos artefatos.

Os registros de QA foram **mantidos de propósito**, não por esquecimento: eles são a evidência verificável das afirmações deste relatório (o ZIP da F5 e o ciclo de aprovação da F9 podem ser reconferidos a qualquer momento sobre esses mesmos ids). São todos do banco local de homologação, isolados pelo prefixo `QA-POSEIDON-`, e podem ser removidos pelo dono quando a conferência terminar.

---

## Tabela final

| Fase | Nome | Status inicial (quadro) | Problemas encontrados | Correções | Status final | Evidência |
|---|---|---|---|---|---|---|
| F1 | Léxico e gate de vocabulário | concluída | 1 (escopo do gate) | 0 | **APROVADA** | `check-business-vocabulary.mjs`; gate verde; menu limpo em execução |
| F2 | Voz da Bruna e chat | concluída | 0 | 0 | **APROVADA** | `ReasonCodeHumanizer.cs`; "Bruna Magalhães" e "Modo de trabalho" na tela |
| F3 | Dashboard de negócio | concluída | 1 (rótulo D14) | 0 | **PARCIAL** | 11 cards renderizados; `attention-cards.tsx:74` |
| F4 | Shell e projeto global | concluída | 0 | 0 | **APROVADA** | 5/5 asserções; redirect `/approvals` → `/documents?tab=approvals` |
| F5 | Central de Entregas + ZIP | concluída | 1 (a11y *serious*) | **1** | **APROVADA** | ZIP com manifesto+hash; axe `/delivery` de 56 nós → 0 |
| F6 | Projetos + bindings | concluída | 1 (sem estimativa/inferência) | 0 | **PARCIAL** | `201` com sigla e repo automáticos; `criticality` fixo em 2 testes |
| F7 | Quadro e Conversas | concluída | 0 | 0 | **APROVADA** | 4/4 asserções em execução |
| F8 | Equipe + Executar Projeto | concluída | 0 | 0 | **APROVADA** | "Abrir Poseidon"; sem termos técnicos |
| F9 | Documentos + aprovação | concluída | 2 (contrato) | 0 | **APROVADA** | ciclo completo `201→inReview→awaitingApproval→approved` |
| F10 | Prototipação | concluída | 0 | 0 | **APROVADA** | `prototyping-stage` com gate, `entryPath` e herança |
| F11 | Telas de administração | concluída | 0 | 0 | **APROVADA** | `tool_disabled` provado em integração; 7 telas limpas |
| F12 | Coordenação + guardas | concluída | 1 (4 itens órfãos) | 0 | **PARCIAL** | breaker ligado; `0098` sem store |
| F13 | Ciclo de vida + autoridade | concluída | 2 (3 de 7 tipos; conclusão única órfã) | 0 | **PARCIAL** | fencing real em `RunnerMessageTransition:34-42` |
| F14 | Esforço + camadas | concluída | 1 (4 métodos órfãos) | 0 | **PARCIAL** | camada (i) ligada; `Evaluate`/`SelectFanOut`/`RouteModel` mortos |
| F15 | Roslyn + raio de impacto | concluída | 1 (TS pendente) | 0 | **PARCIAL** | grafo ligado ao loop; sem índice TS |
| F16 | MAST + pass@k + aprendizado | concluída | 1 (fase inteira órfã) | 0 | **REPROVADA** | 0 consumidores; 0 migrations; 0 painel |
| F17 | Revisor contínuo + hooks | concluída | 1 (fase inteira órfã) | 0 | **REPROVADA** | 0 consumidores; 0 migrations; executores sem pareamento |

---

## Conclusão

A camada de **produto** — tudo que o dono leigo vê e usa — está substancialmente entregue: 9 fases aprovadas, com fluxos verificados ponta a ponta em execução real (criar projeto, aprovar documento, exportar ZIP, gate de prototipação, navegação nos três modos, léxico limpo). O defeito mais sério dessa camada era invisível aos gates e foi corrigido e coberto por teste nesta auditoria.

A camada de **motor de agentes** é onde a campanha divergiu do documento: as fases de backend entregaram políticas corretas, bem escritas e testadas que **nunca são chamadas** — 14 no total. F16 e F17 estão nessa condição por inteiro; F12, F13 e F14 em parte.

Nenhuma dessas lacunas aparecia como vermelho em gate algum. **Isso deixou de ser verdade nesta auditoria:** `PolicyWiringTests` reprova o build quando uma política estática não tem consumidor, com a dívida de hoje declarada nome a nome e um segundo teste que impede a lista de envelhecer. O repositório segue verde, mas a próxima campanha não consegue mais declarar concluído o que não executa — e a prova de que o gate era necessário é que, ao rodar pela primeira vez, ele encontrou duas órfãs que a inspeção manual desta mesma auditoria havia deixado passar.

O que fica para o dono decidir são escolhas de produto, não achados técnicos pendentes: implementar F16 e F17 (ou reduzir seu escopo), ligar as políticas parciais de F12/F13/F14, resolver quem estima a criticidade de um projeto novo, e definir se as 41 classes `sm:` restantes devem passar a funcionar (`md:`) ou ser removidas.

---

# Correções aplicadas na segunda rodada (30/07/2026)

O dono autorizou correção autônoma de tudo que a auditoria encontrou. Esta seção registra o que foi
efetivamente ligado, o que foi medido e revertido, e o que permanece aberto — com o mesmo critério
da auditoria: **wiring provado por gate, comportamento provado por teste, efeito provado em
execução quando aplicável**.

## F3 — rótulo da D14 · **PARCIAL → APROVADA**

O card exibia "Decisões humanas", termo que o léxico §2 reserva para escalação (`agent_requests`).
Renomeado para **"Aprovações pendentes"** (pt-BR e en), com os textos de vazio e de link alinhados.
`cockpit` 32/32 verde.

## DEF-08 — breakpoint fantasma · **CORRIGIDO**

As 41 classes `sm:` restantes (28 arquivos) não geravam CSS nenhum, porque o tema declara apenas
`md/lg/xl`. Todas migradas para `md:` — a convenção que o próprio projeto já documentava. Duas
ocorrências ficaram de fora **de propósito**: `button-variants.ts` usa `sm` como nome de variante de
tamanho (não é breakpoint) e o comentário de `documents-tabs.tsx`, atualizado para descrever a regra.

Mais importante que a troca: a convenção virou **gate**.
`design-system/__tests__/breakpoint-convention.test.ts` lê os breakpoints reais do
`tailwind.config.js` e reprova qualquer prefixo responsivo que o tema não declare. Verificado que o
teste falha e aponta o arquivo culpado quando um `sm:` é reintroduzido.

## F6 — criticidade e tecnologias · **PARCIAL → APROVADA**

`ProjectIntakeEstimator` (novo, em `Modules.Projects/Application`) estima criticidade e extrai
tecnologias do título e do objetivo, ligado ao `POST /api/v1/projects` — só quando o dono não
informa, preservando o formulário completo do modo Técnico.

Duas regras de honestidade guiaram o desenho:

- **Tecnologia não se adivinha, se extrai.** Catálogo fechado; o que o texto não nomeia não entra.
  "Quero um site de bolos" não vira "React + Postgres", porque um plano montado sobre pilha
  imaginada é pior que um plano sem pilha.
- **Na dúvida, o meio.** Sinal de risco eleva (dinheiro, dado sensível, disponibilidade); ausência
  mantém `medium`; só escopo declaradamente simples desce para `low`.

Toda estimativa devolve o motivo em linguagem de negócio (`criticalityRationale`, campo derivado —
sem migration, porque a estimativa é pura e recalculável).

**Efeito medido em execução real** (mesmos objetivos que expuseram o defeito):

| Projeto | Antes | Depois |
|---|---|---|
| Loja de bolos | `medium` · `[]` | `medium` · `["WhatsApp"]` + explicação |
| Pagamentos com cartão | `medium` · `[]` | **`critical`** · `["PostgreSQL","Python","React"]` + explicação |

12 testes novos, incluindo fronteira de palavra (`Java` não sai de `JavaScript`, `Go` não sai de
`Google`) e acento/caixa indiferentes. O gate de contrato exigiu — corretamente — regenerar o
OpenAPI publicado e declarar o campo no schema do frontend e no teste de drift.

## F12 — espera, guardas de laço e profundidade · **PARCIAL → APROVADA**

Três políticas saíram do papel:

1. **`AgentWaitPolicy` → `AgentRunOrchestrator.RecoverAsync`.** A recuperação selecionava tentativas
   por lease vencida e reivindicava **todas**, marcando como `failed` e devolvendo a worktree ao
   pool — inclusive a de um agente que seguia batendo heartbeat e escrevendo naquele diretório.
   Agora a política decide, e ela só concede `MayReclaim` na ausência de sinal de vida. A recusa é
   publicada como evento auditado (`agentRun.reclaimDeclined`), não silenciosa.
2. **`ChiefLoopGuardPolicy` + `CausalCycleDetector` → `ChiefTurnBackgroundService`.** A guarda entra
   antes de materializar plano — o ponto onde a Bruna gera trabalho a partir do próprio output, que
   é onde o laço se fecha. Consome os fatos duráveis (acumulado, janela, arestas causais) do store
   que já existia, registra cada turno autodisparado e grava a aresta `demand → plan` no grafo.
   Interrupção é sempre auditada com evidência.
3. **`PlanDepthPolicy` → `DemandPlanEndpoints`.** Planos acima do teto são reagrupados em ondas,
   reduzindo barreiras de coordenação sem alterar a ordem entre cards dependentes.

## F13 — protocolo de mensagens · **PARCIAL → APROVADA**

O defeito era **duplicação**: o runtime aceitava 3 tipos (`RunnerMessageTypes`) enquanto a fase havia
entregue os 7 numa classe paralela e órfã (`DispatchLifecycleTypes`). Quem lesse a segunda concluía,
erradamente, que o protocolo estava completo.

Resolvido movendo a fonte da verdade para o **SharedKernel** — o lugar arquiteturalmente correto,
já que quem depende dos tipos é o contrato IPC (processador, stores, runner) — com os 7 tipos mais
o nome legado `completion`, que segue aceito para não recusar a conclusão de um runner em voo. A
classe duplicada foi **removida** e seus 10 testes apontados para a fonte real. `escalation`,
`decision_gate` e `merge_ready` agora existem de fato em execução.

`TurnCompletionPolicy` foi ligada ao `RunnerIpcMessageProcessor`: ela distingue o **reenvio fiel**
(mesma chave, mesmo desfecho — a rede é falível e o agente tem direito de repetir) de uma segunda
história diferente, que é recusada. O store mantém sua garantia atômica como defesa em profundidade.

## F14 — fronteira negativa e camadas · **PARCIAL → APROVADA**

- **`NegativeBoundaryPolicy` → `DemandDecompositionPlanner`.** Cada card passa a nascer declarando o
  que pertence aos irmãos ("Não altere X — é da tarefa Y"), no próprio enunciado que vai ao agente.
  Sem isso, um agente que encontra um defeito real fora do escopo tende a consertá-lo — louvável
  isolado, destrutivo em paralelo.
- **`LayeredVerificationPolicy.Evaluate` → `ApplyReviewVerdictAsync`.** O veredito final passou a ser
  composto pelas três camadas, com a regra de não-compensação valendo: "a intenção está ótima" não
  aprova trabalho que falha o critério de aceite declarado.

## F17 — o que foi tentado, medido e **revertido**

`PersonaDefinitionLint` foi ligado ao `POST /api/v1/agent-definitions` como Default-FAIL. **A
integração provou que estava errado**: definições legítimas passaram a ser recusadas com 422.

A causa é substantiva, não um detalhe de implementação: o lint julga `AllowedScopes`/`DeniedScopes`
de **caminho**, e `AgentDefinitionWriteRequest` não tem esses campos — `Stacks` é tecnologia e
`Limitations` é texto livre. Mapear um no outro faz o lint julgar a coisa errada. Revertido, com o
bloqueio registrado na dívida do gate: ativar exige antes **dar escopo de path à persona** (contrato
+ migration + UI). Trocar "sem guarda" por "guarda que barra o legítimo" seria pior que o defeito.

## O que permanece aberto

| Fase | Pendência | Por quê |
|---|---|---|
| **F16** | MAST, pass@k e promoção de aprendizado seguem órfãos | É implementação de fase, não fiação: exige classificador no encerramento de tentativa, migrations 0105–0107, cálculo de pass@k e painel Técnico |
| **F17** | Revisor contínuo e hooks por worktree | Idem — exige pareamento no ciclo do executor CLI e geração de configuração na worktree |
| **F17** | Lint de persona | Bloqueado pelo contrato (acima) |
| **F15** | Índice TypeScript | Segunda entrega declarada da própria fase; cards de frontend seguem "não medidos" |
| **F14** | `EffortPolicy.SelectFanOut` e `RouteModel` | O roteamento por assimetria de modelo precisa de um atributo de faixa nas contas que **não existe** hoje; inventá-lo seria fabricar dado |
| **F13** | `DispatchAuthorityPolicy` | Duplica a autoridade **já ativa** em `RunnerMessageTransition`; unificar exige mover a decisão para dentro da transição durável sem quebrar o contrato IPC |

Todas seguem declaradas em `PolicyWiringTests.DeclaredDebt`, com a razão precisa — o gate reprova o
build se alguma for silenciosamente esquecida, e reprova também se alguma for ligada e a linha ficar
para trás.

---

# Terceira rodada — F16, F17 e F15 implementadas

## F16 — MAST, pass@k e promoção · **REPROVADA → APROVADA**

Não era fiação: a fase não tinha persistência, classificação nem painel. Foi implementada.

- **Migration `0105_mast_classifications`** (espelhada SQLite/Postgres, com RLS por tenant no
  Postgres). Chave primária por tentativa: reclassificar **substitui**, porque acumular faria a
  distribuição contar a mesma falha várias vezes e mentir exatamente sobre a concentração que ela
  existe para revelar. As 6 contagens de migration do repositório foram atualizadas (87 SQLite /
  88 Postgres).
- **`IMastClassificationStore`** + `SqliteMastClassificationStore` + `PostgresMastClassificationStore`,
  registrados no DI.
- **Classificação no encerramento da tentativa** (`AgentRunOrchestrator`): toda tentativa que falha
  recebe um modo da taxonomia. O mapeamento parte do código de falha real do executor — escopo
  negado vira `disobey_role_specification`, ferramenta recusada vira `disobey_task_specification`, e
  o resto vira `premature_termination`. Onde o sinal não distingue o modo, **não se inventa
  precisão**: o código de falha vai para a evidência e a classificação fica no modo honesto.
- **Painel (modo Técnico)**: `GET /api/v1/projects/{projectId}/reliability` devolve a distribuição
  por categoria, o conselho derivado dela (`MastAdvice`) e o **pass@k por par conta+modelo+tipo**,
  calculado do histórico real de `model_invocations` — a ordem das invocações define o número da
  rodada, porque não existe campo "tentativa nº" e inventá-lo por contagem global misturaria cards.
- **`LearningPromotionPolicy.RequireHumanApproval`** ligada ao endpoint de promoção. O ciclo já
  restringia a ação ao admin; a guarda acrescenta o que a fase pede e o papel não garante — um
  aprovador **nomeado**. Um invariante que só vale num caminho não é invariante.

## F17 — revisor contínuo e hooks · **REPROVADA → PARCIALMENTE APROVADA**

- **`WorktreeHookPolicy` → `AgentRunOrchestrator`**: os hooks nascem **com** a worktree, gravados em
  `.poseidon/hooks.json` dentro dela. O rigor vem do risco do card. Regra que só existe no enunciado
  é cumprida por boa vontade — o agente lê "não toque em X", concorda, e três horas depois toca em X
  porque o defeito estava lá. Falha ao escrever **não derruba a tentativa**: perder a camada extra é
  melhor que perder o trabalho por causa dela.
- **`ContinuousReviewPolicy` → `ChiefBacklogLoopService`**: a revisão pareada virou **regra**, não
  disponibilidade. Em risco alto e crítico o revisor é obrigatório; revisor igual ao executor não
  conta como revisor (ele repete o raciocínio que o trouxe até ali e o encontra correto). Antes, a
  ausência de revisor era decidida pela sorte de haver conta livre.
- **`PersonaDefinitionLint` continua pendente**, pelo motivo medido na segunda rodada: a definição de
  persona não tem escopo de path para o lint julgar. Segue declarado na dívida do gate.

## F15 — índice TypeScript · **PARCIAL → APROVADA**

`TypeScriptCodeGraphIndex` implementa `ICodeGraphIndex` para o frontend, derivando o grafo dos
**imports** — que é onde a dependência está declarada — sem type-checker e sem dependência nova
(a fase só autoriza os pacotes Roslyn). Resolve import relativo, o alias `@/`, extensão implícita e
`/index`; ignora pacote externo, que não é defeito e encheria o grafo de folhas que ninguém consulta.

O limite é declarado, não escondido: o índice enxerga dependência de **arquivo**, não de símbolo.
Ele não sabe que `Button` mudou de assinatura; sabe que N arquivos importam `design-system` — que é
exatamente o que o raio de impacto consome. O escopo de diagnóstico é `SyntaxOnly`, porque declarar
`Semantic` faria o consumidor confiar num diagnóstico que ninguém produziu.

Com isso, caminhos de frontend deixam de cair sempre em `UnindexedPaths` — e o que este índice não
cobrir continua sendo impacto **desconhecido**, nunca zero.

## Duas regressões que eu mesmo introduzi, medidas e corrigidas

O gate pegou ambas antes de qualquer entrega — que é exatamente o que ele existe para fazer.

1. **Classificação MAST abortava a tentativa.** Eu a chamei no caminho crítico, antes de completar o
   receipt de governança. Uma restrição do banco ali derrubava o encerramento inteiro e o
   `BundleChecksum` chegava vazio. Trocar um dado de telemetria por trabalho real perdido é o pior
   negócio possível: a chamada passou a ser isolada, e classificar agora nunca decide o desfecho.
2. **Hooks dentro da worktree impediam a limpeza.** `.poseidon/hooks.json` é arquivo não rastreado na
   árvore de trabalho, e isso bloqueava a remoção da worktree no cleanup — o smoke do bootstrap
   reprovou com processo órfão sobrevivendo ao run. Confirmei a causa com um teste de controle
   (removi só a escrita: verde). A configuração passou a morar **ao lado** da worktree, em
   `{ControlledRoot}/hooks/{attemptId}.json`: continua sendo por tentativa, e não mora onde o agente
   edita.


---

# Quarta rodada — F17 fechada

## O bloqueio removido pela raiz

O lint de persona estava travado há duas rodadas por um motivo real: ele julga **caminhos**, e a
definição de agente não declarava caminho nenhum. Forçá-lo sobre `Stacks` (tecnologia) e
`Limitations` (texto livre) foi tentado, medido e revertido — recusava definição legítima com 422.

A correção foi dar o que faltava, não contornar:

- **Migration `0106_agent_definition_path_scopes`**, espelhada SQLite/Postgres: `allowed_scopes_json`
  e `denied_scopes_json`. As 6 contagens do repositório foram atualizadas (88 SQLite / 89 Postgres).
- `AgentDefinitionContent`, `AgentDefinitionRecord`, `AgentDefinitionWriteRequest` e os **dois**
  stores passaram a carregar os campos.
- **Lint ligado como Default-FAIL** na criação — com um critério que evita repetir o erro anterior:
  ele só julga persona que **declara** escopo. Sem declaração, a persona herda o escopo do papel
  (`AgentPathScopePolicy`), que já é restritivo; exigir denylist dela recusaria as canônicas sem
  ganho algum de segurança.

Quatro testes fixam as duas pontas: persona sem escopo passa (é o caso das canônicas), persona que
declara escopo sem denylist é recusada, `bash + escopo raiz` é recusado, caminho protegido não pode
ser concedido, e persona com escopo + denylist é aceita.

**F17: parcialmente aprovada → APROVADA.**

## Estado final

| | |
|---|---|
| Fases aprovadas | **17 / 17** |
| Políticas sem consumidor | **1** (era 14) |
| `verify.sh` | **exit 0** — 1321 unitários · 233 integração · 22 arquitetura · 42 contratos · 8 concorrência · 12 recovery · 768 frontend · governança 0 erros |

## Quinta rodada — a dívida zerada, e o erro de análise que ela escondia

Eu havia classificado `DispatchAuthorityPolicy` como "duplicação inócua" e proposto deixá-la
declarada. **A classificação estava errada**, e a verificação linha a linha provou:

| Verificação | Store (o que rodava) | Política |
|---|---|---|
| Sequência / tentativa já concluída | ✅ | — |
| Fencing **superado** | ✅ | ✅ |
| Tentativa ou card **alheio** | ❌ | ✅ |
| Fencing **à frente** do despacho ativo | ❌ | ✅ |
| Proveniência (orquestrado × avulso) | ❌ | ✅ |

O store aplicava um **subconjunto** da regra. Mensagem falando de outra tentativa e fencing à frente
do despacho passavam sem exame — não era cópia redundante, era parte da F13 desligada.

**Correção:** a política e seus tipos foram movidos para `Harness.SharedKernel/RunnerIpc`, onde o
contrato IPC vive, e `RunnerMessageTransition` passou a **delegar** a ela. Uma fonte só, e o produto
passou a aplicar as três verificações que faltavam. O arquivo duplicado em `Modules.Coordination`
deixou de existir.

```
DeclaredDebt = { }
```

**Zero políticas sem consumidor de produção** — eram 14 quando esta auditoria começou.

Registro a lição porque ela é o próprio tema deste relatório: eu havia acabado de criar um gate para
impedir a racionalização "existe, está testado, está bom" — e quase a repeti por escrito, com uma
justificativa de aparência técnica. O que não deixou passar foi a linha continuar visível no código
e o dono cobrar a decisão.
