# 01 — POSEIDON: INVENTÁRIO, MATRIZ DE CARREGAMENTO E LACUNAS
Consolida: 01-INVENTORY-SUMMARY, 01-DOCUMENT-GRAPH, 01-LOADING-MATRIX, 01-GAPS. CSV em anexo separado.

**Auditoria:** `origin/develop` @ `bed4d8ce98ec5def44e32e9a477faf341a85a3b7` (2026-07-19T18:08:51-03:00, 190 commits). HEAD do repo: `de2190ac...`. Branches remotas: somente `develop` visível no clone single-branch; README confirma política main+develop. Clone separado somente leitura em ambiente isolado; nenhuma escrita no repositório. Limitação: análise de `main` não realizada (fora do escopo: develop é a integração); tamanhos em linhas; tokens estimados a ~11 tokens/linha (PT-BR + código misto).

## 1. Totais

1.228 arquivos versionados. Por extensão (top): 450 `.cs`, 187 `.ts`, 156 `.tsx`, 155 `.md`, 74 `.gitkeep`, 70 `.sql`, 66 `.json`, 29 `.csproj`, 15 `.sh`. Markdown total: 4.948 linhas (~55–65k tokens). Por diretório (top): `frontend/src` 376, `src/Modules` 159, `docs/backend` 129 (122 em `execution/`, dos quais 113 são evidências), persistência SQLite/Postgres 87+87, `tests/*` 140, `tools/backend` 17 scripts, `docs/decisions` 14 ADRs.

## 2. Artefatos de governança encontrados (categoria x veredito preliminar)

| Artefato | Categoria | Autoridade | Situação | Recomendação |
|---|---|---|---|---|
| `docs/backend/execution/CURRENT_STATE.md` (129 l, 2026-07-19) | Estado/handoff | Projeção (banco deveria ser autoritativo) | Excelente como handoff; mistura estado + suposições + riscos + instruções + **caminhos absolutos `/Users/mateus/...`** | Manter; tornar projeção gerada; banir paths absolutos |
| `docs/frontend/CURRENT_STATE.md` (2026-07-18) | Estado/handoff | Projeção | FE-0..FE-4 concluídas; contém **regra de coordenação multiagente sem enforcement** ("git add apenas frontend/...") | Converter regra em hook/CI |
| `PROGRESS.md`, `ROADMAP_PROGRESS.md` | Progresso | Projeção | 4 trilhas (Implementado 99,5 / Validado 99,3 / Integrado 96,4 / Homologado 0) — boa disciplina | Manter; gerar de evidências |
| `MASTER_PLAN.md`, `RISKS.md`, `DECISIONS_PENDING.md`, `ENVIRONMENT.md`, `PHASE_0_SCOPE.md`, `DESKTOP.md` | Execução | Normativo/descritivo | Vivos e datados | Manter |
| `docs/decisions/ADR-001..014` | Decisão | Normativo | Completos, nomeados por tema | Manter; adicionar índice no manifest |
| `docs/architecture/CONTEXT_MAP.md`, `INVARIANTS.md`, `MODULE_CATALOG.md` | Arquitetura | Normativo | Presentes | Manter; carregar por bundle |
| `docs/security/THREAT_MODEL.md` (07-18) e `docs/backend/security/THREAT_MODEL.md` (07-19) | Segurança | **Fontes concorrentes — divergem (diff confirmado)** | Conflito real | **Consolidar em um; o outro vira redirect ou some** |
| `docs/backend/execution/evidence/*` (113) | Evidência | Evidência | Densidade colapsa: F1=35, F2=37, F3=4, F4..F8=1–2, F9=4, F10=8, F11=7 | Padronizar evidência mínima por gate |
| `docs/frontend/HANDOFF_API.md` (302 l), `SCREENS.md`, `DECISIONS.md` (337 l, D-001..D-052) | Contrato/decisão FE | Normativo provisório | Reconciliação com OpenAPI registrada em `docs/contracts/CONTRACT_RECONCILIATION.md` | Manter; OpenAPI canônico |
| `tools/backend/*` (verify*.sh, scan-secrets, sbom, semgrep.yml) | Enforcement | Operacional | **Enforcement mecânico já existe** | Ancorar regras documentais nele |
| `README.md` | Entrada humana | Descritivo | Bom, curto | Manter |

## 3. Matriz de carregamento (verificada, não presumida)

| Leitor | O que carrega automaticamente | Encontrado no repo | Resultado hoje |
|---|---|---|---|
| Claude Code | `CLAUDE.md` (raiz/hierarquia), `.claude/rules|agents|skills` | **Nenhum existe** | Carrega nada; comportamento vem só do prompt colado |
| Codex CLI | `AGENTS.md` (hierárquico) | **Não existe** | Idem |
| Kimi Code | convenção AGENTS/contexto do CLI | **Não existe** | Idem |
| Chief do Poseidon (produto) | StatusDigest do banco em runtime | n/a (mecanismo interno) | OK por design |
| Qualquer agente | O que o prompt mandar ler | prompts **fora do repo** | Ver lacuna L1 |

## 4. Grafo documental

Medição: **0 links markdown relativos** entre os documentos de `docs/` e `README.md`. O grafo é totalmente desconexo; discovery é 100% por convenção de nome ditada pelos prompts externos. Consequências: nenhum caminho garantido até `INVARIANTS.md`/ADRs a partir de um entrypoint; órfão é indetectável mecanicamente (sem índice, todo documento é tecnicamente órfão); impossibilidade de medir "documento entregue vs. utilizado".

## 5. Lacunas (ordenadas por risco)

- **L1 — Fonte de verdade fora do Git:** `CURRENT_STATE.md` declara como autoridade `/Users/mateus/Downloads/PROMPT_ORIGINAL_CODEX_BACKEND_POSEIDON_v1.3_COM_ADENDO.md` + "prompt de continuidade" no mesmo diretório; há ainda referências a "refinamentos v3 §8". Prompts não versionados, numa pasta volátil (Downloads), numa única máquina, com histórico de versões invisível. Risco: perda, drift, irreproduzibilidade de qualquer turno.
- **L2 — Zero entrypoints de agente versionados** (tabela acima): aderência depende de colagem manual correta do prompt certo na versão certa.
- **L3 — Grafo desconexo + ausência de índice/manifest** (seção 4).
- **L4 — Fontes concorrentes:** THREAT_MODEL duplicado e divergente.
- **L5 — Regra sem enforcement:** convenção de `git add` escopado entre Codex e Kimi vive em prosa; um `pre-commit` hook a tornaria mecânica. Agravante: incidente **R-013** (token Telegram observado em linha de comando) mostra que scan-secrets existe mas não previne exposição em processo.
- **L6 — Evidência com densidade decrescente** por fase (seção 2) — o custo de produzir evidência venceu a disciplina nas fases tardias.
- **L7 — Estado não portável:** caminhos absolutos e nomes de máquina no handoff.
- **L8 — Sem camadas de memória/aprendizado:** nenhum mecanismo de learning candidate; erros repetidos só são evitáveis se alguém editar o prompt externo.

## 6. Pontos fortes a preservar

Disciplina de evidência por gate (mesmo decaindo, é rara); ADRs completos; quatro trilhas de progresso calculadas; scripts de verificação/segurança já mecânicos; handoffs CURRENT_STATE que cumpriram o contrato de retomada na prática (frontend e backend retomaram entre sessões reais); convenção de branches respeitada (só main+develop).
