# Diário da operação — TrensRJ Poseidon Evaluation 2026

Registro corrido do que acontece na janela oficial (05/08 → 20/08). Complementa
`docs/operations/trensrj-evaluation-window.md` (registro formal: baseline, ProjectIds, métricas)
e `docs/operations/incidents/` (um arquivo por defeito observado, formato INC-EVAL).

---

## Dia 1 — 06/08/2026

### Linha do tempo

| Hora (UTC) | Evento |
|---|---|
| 01:02 | Kickoff do **Prisma** enviado pelo chat; projeto despausado 01:04 |
| 01:08 | Bruna responde: Triagem, 8 demandas, 0 ASK bloqueante (prazo classificado como DEFER) |
| 01:40 | Kickoff do **Indicadores**; despausado 01:42 |
| 01:43 | Bruna responde com 1 ASK genuíno: escopo integral × prazo |
| 01:44 | **1ª decisão humana de produto**: escopo integral fixo, ordenação núcleo-primeiro, feature complete interno 13/08 |
| 04:44–05:30 | **INC-EVAL-001**: `--effort`/`--model` inválidos travam o Conselho da fase 4 |
| 06:15 | Fase 4 fecha nos dois projetos; entra a fase 5 |
| 06:21–09:10 | **INC-EVAL-002**: card `critical` sem persona elegível + vazamento de claim de workspace (tempestade de `scopeconflict`) |
| 12:00–13:09 | **INC-EVAL-003**: o Quadro escondia a coluna Concluída (achado pelo dono); fix, gates verdes e publicação segura às 13:08 |
| 13:47 | Card de RBAC escala por orçamento de rodadas; abre pedido de atenção humana |
| 17:59 | Decisão humana: replanejar — **aplicada e revertida no mesmo minuto** (**INC-EVAL-004**) |
| 18:27 | Decisão corrigida: fatiar o card em 4 menores; Bruna executa às 18:28 |

### Anatomia das 343 tentativas do dia

| Categoria | Tentativas | Tokens de saída |
|---|---:|---:|
| Morreram por defeito de plataforma (`scopeconflict`, persona, model/effort) | 257 | 0 |
| Estouro de cota | 10 | 0 |
| Canceladas | 8 | 0 |
| **Executaram e foram aprovadas** | **45** | 1.232.593 |
| Executaram e foram reprovadas em review | 22 | 1.511.018 |

Tempo com algum agente de fato rodando: **620 min de 1.289** (48% da janela).
Aprovadas por fase: Triagem 2 · Descoberta 6 · Arquitetura 14 · Planejamento 18 ·
**Desenvolvimento 5**.

Reprovações em review por causa: `acceptanceNotMet` 12 · `qualityBar` 7 · `scopeViolation` 2 ·
`contextMissing` 1.

### Quem executa o quê (medido no ledger, não declarado)

| Papel | Conta / provider | Modelo · esforço |
|---|---|---|
| Produção de cards (fases 1–4) | `chief-claude-primary` e `worker-claude-secondary` (anthropic) | `opus` · `medium` |
| Produção de cards (fase 5) | as mesmas duas contas | `opus` · `medium` nas tentativas mais antigas; **sem override** (default da conta) nas posteriores a INC-EVAL-001 |
| **Code review** | `worker-antigravity-review` (antigravity) — 40 reviews, 31 aprovações, 9 reprovações | sem override de modelo |
| Gates determinísticos | `deterministic-delivery-gates` (12 reprovações) e `deterministic-document-gate` (1) — plataforma, não modelo | — |
| Conselho da fase 4 | pareceres produzidos por `worker-claude-secondary` (anthropic) e `worker-antigravity-review` (antigravity) | idem |
| Codex | `worker-codex-critic` (3 invocações) e `worker-codex-frontend` (1) | participação marginal |

**O Antigravity está realmente sendo usado**: 311 invocações, 18.015 tokens de saída, 33 s de
duração média, e 249 reviews com parecer textual substantivo (>200 caracteres). Ele é hoje o
revisor principal da fábrica.

> Nota sobre `opus`/`medium`: o override foi REMOVIDO das rotas dos dois projetos em INC-EVAL-001
> porque a conta rejeitava a combinação. Desde então cada conta executa no seu modelo default.

### Proposta de mudança (análise do supervisor, aguardando decisão do dono)

1. **Destravar a fleet** — hoje 1 conta executa; as outras estão `role_not_allowed` ou
   desabilitadas para os papéis da fase 5. É o teto real de vazão: sem isso, nenhum ajuste de
   software muda o resultado. Ação: revisar `allowedRoles` de `worker-codex-frontend`,
   `worker-kimi-ui` e `worker-glm-general` para os papéis backend/frontend da fase 5.
2. **Encurtar a esteira documental da fase 5** — 40 das 45 aprovações do dia foram documentos das
   fases 1–4. Para um prazo de 15 dias, o Playbook deveria admitir um perfil "entrega rápida" nos
   cards de código (briefing técnico + dicionário ubíquo bastam; DORA e code review estruturado
   podem ser derivados ao fim da fase).
3. **Cards menores por padrão na fase 5** — o card de RBAC provou o ponto: card grande consome as
   4 rodadas antes de acertar. Fatiar por camada (persistência → domínio → aplicação → endpoints)
   funcionou e deveria ser a regra de planejamento, não a exceção pós-escalonamento.
4. **Rever o teto de rodadas junto com o replan** (INC-EVAL-004): enquanto a decisão humana não
   reabrir orçamento, todo card que esgota vira descarte + refatiamento manual.

## Dia 2 — 07/08/2026 — Recuperação de throughput da Fase 5

Missão noturna (autorizada com autonomia total, exceto iniciar o Poseidon): investigar, documentar
e corrigir os achados do Dia 1, sem tocar Playbook/Fases 1-4. Sete fixes reais commitados em
`develop`, verificados (build+teste, 2290 testes) antes de qualquer aplicação ao vivo:

1. Retry continuity — `CorrectionBaseBranch` comparava com a string errada (`operational_state`
   nunca contém `"rejected"`); toda correção pós-review reimplementava do zero.
2. ScopeClaim liberada nos 3 pontos de rejeição pós-acquire (era a causa do storm do INC-EVAL-002).
3. INC-EVAL-002 fechado: Priority↔RiskTier corrigido em 7 pontos + clamp de elegibilidade de
   persona (card crítico cabe em persona de teto `high`, sem migração de schema).
4. INC-EVAL-004 fechado: orçamento de rodadas reseta por época de replanejamento aprovado.
5. Card Slice Readiness Gate novo (`dor.card_too_large`).
6. INC-EVAL-001 fechado: erro de `--model`/`--effort` inválido não é mais classificado como cota.
7. Bug de roteamento de fleet corrigido: personas novas paravam de herdar a conta do Chief por
   acidente de ordenação.

**Achado dentro da própria missão**: 3 forks paralelos ignoraram instrução de "só leitura" e
escreveram concorrentemente no mesmo arquivo (um chegou a quebrar o build) — revertido,
re-investigado com cuidado e refeito com disciplina de dono único por arquivo.

**`SupersedeTaskAsync`** — mutação de domínio nova (migração 0134), com ledger e auditoria, para
encerrar formalmente card `escalated` substituído por cards menores sem `UPDATE` cru. Usada para
formalizar RBAC backend e frontend de Indicadores — descoberto que uma decisão real do dono no
chat (2026-08-07T00:14 UTC) já tinha materializado 5 dos 9 cards de substituição, órfãos pelo
mesmo bug do INC-EVAL-004.

**Restart real** (autorizado explicitamente pelo dono, de manhã, com supervisão): Docker Desktop
não estava rodando — o Host recusa subir sem ele (fronteira de sandbox obrigatória); resolvido,
registrado como dívida de preflight (`./poseidon start` deveria checar Docker/portas/disco ANTES
de começar a subir, não descobrir no meio). `INC-EVAL-007` — os dois projetos estavam
`projects.state='paused'` (camada diferente de `workflow_runs.state`, que já mostrava `running`),
bloqueando dispatch com zero log por ~12 minutos; destravado via `PATCH /api/v1/projects`.

**Achado ao vivo, fora do escopo original**: `plan_graph.shared_scope_undeclared` bloqueava cards
sem superfície de código reconhecida (artefatos de suporte, CORRECAO/DoD) uns contra os outros —
dois fallbacks de escopo amplo idênticos não são evidência de colisão real. Corrigido, testado,
verificado ao vivo (bloqueios sumiram do log pós-fix).

**`INC-EVAL-006`** — `worker-codex-frontend` (provider Codex/OpenAI) recebeu `model_id` resolvendo
para `opus` (modelo Anthropic) — 1ª ocorrência, classificada corretamente como
`account_model_unsupported` (não mais `quota_limited`), não corrigida ainda por decisão do dono
(gatilho: só agir na 2ª ocorrência).

Decisão do dono ao final do dia: **não pausar, não reiniciar, não mexer em scheduler/Playbook/Graph
— janela de observação de 60 minutos com os dois projetos `RUNNING`**, medindo funil de dispatch,
paralelismo real, pré-execução e continuidade de retry antes de qualquer novo ajuste estrutural.
