# 03 — ANÁLISE: ECC E OH MY PI
Consolida: 03-ECC-INVENTORY, 03-ECC-ADOPTION-MATRIX, 03-OMP-CAPABILITY-INVENTORY, 03-OMP-POSEIDON-MAPPING, 03-LICENSE-AND-ATTRIBUTION-REVIEW, 03-CROSS-REPOSITORY-COMPARISON. CSV da matriz OMP em anexo.

## 1. ECC — affaan-m/ECC

**Fixação:** SHA `0071fa5c3c389d2b4b235a39402c891e146cdef3` (último commit 2026-07-19 — mantido ativamente, atualizado no próprio dia da auditoria). Licença MIT (c) 2026 Affaan Mustafa. **Alegação de vencedor de hackathon: NÃO verificada nesta pesquisa** — não valide por README; exigiria fonte do organizador (pendência registrada).

**Inventário:** 2.495 arquivos .md. Raiz: `CLAUDE.md`, `AGENTS.md`, `RULES.md`, `SOUL.md`, `WORKING-CONTEXT.md`, `SECURITY.md`, `TROUBLESHOOTING.md`, `COMMANDS-QUICK-REF.md`, `agent.yaml`, `contexts/`, `commands/`, `config/`. Adapter cross-harness: `.codex/AGENTS.md` + `.codex/agents/` + `config.toml`. `agents/`: ~60+ personas .md, incluindo `chief-of-staff`, `code-reviewer`, revisores por linguagem (csharp, cpp, django, flutter, fsharp...), `build-error-resolver` por stack, `agent-evaluator`, `doc-updater`, `conversation-analyzer`. `skills/`: **278** diretórios com SKILL.md (de `accessibility` a `agentic-os`). `scripts/`: `harness-audit.js`, `catalog.js`, `control-pane.js`, `codex-git-hooks/`, `auto-update.js`, `consult.js`, CI. `examples/CLAUDE.md` abre com um bloco **"Prompt Defense Baseline"** (anti prompt-injection: homoglifos, zero-width, urgência, autoridade falsa, conteúdo de ferramenta com comandos embutidos) seguido de regras de organização de código (arquivos pequenos, 200–400 linhas, organização por domínio).

**Leitura crítica:** o ECC é simultaneamente o melhor catálogo público de PEÇAS do gênero e a demonstração viva do antipadrão de escala — 2.495 documentos e 278 skills é exatamente o cenário que a missão manda evitar. O valor está em peças individuais, não na estrutura.

**Matriz de adoção (candidato -> problema que resolve -> decisão -> justificativa):**

| Candidato ECC | Problema | Decisão | Justificativa |
|---|---|---|---|
| Padrão canônico + `.codex/AGENTS.md` gerado | Multi-harness sem duplicação | **Adaptar** | Núcleo do doc 04; gerar por script, não copiar conteúdo |
| Bloco Prompt Defense Baseline | Prompt injection via conteúdo | **Adaptar (reescrever)** | Curto, barato, entra na Camada 0; reescrever no vocabulário Poseidon |
| `harness-audit.js` (conceito) | Docs órfãos/quebrados | **Reimplementar** | Vira o doc-linter do manifest, em Node ou .NET, com regras próprias |
| Frontmatter de agents .md (persona por arquivo, com metadados) | Personas versionadas | **Adaptar formato** | Compatível com AgentDefinition do produto |
| Revisores por linguagem, build-resolvers | Especialização de critic | **Inspiração** | Poseidon deriva critic de RiskTier + stack do projeto; não importar 60 personas |
| `codex-git-hooks` | Regra sem enforcement | **Inspiração** | Confirma L5; hooks próprios (escopo de git add, scan de segredo em pre-commit) |
| 278 skills | — | **Rejeitar importação em massa** | Sprawl; importar apenas sob demanda comprovada, uma a uma, com teste |
| SOUL.md / WORKING-CONTEXT.md | Identidade/contexto de trabalho | **Inspiração parcial** | Conteúdo equivalente já existe (ChiefDefinition, CURRENT_STATE); não duplicar |

Licença MIT permite copiar com atribuição; ainda assim, a decisão dominante é adaptar/reimplementar — copiar texto normativo alheio cria regra sem dono.

## 2. Oh My Pi (omp)

**Fixação:** SHA `39c95e5e29b1c8b082059f57421ce445c3dffdd4` (2026-07-19), release mais recente observado v16.3.4; MIT (c) 2025 Mario Zechner, (c) 2025–2026 Can Bölük; fork do Pi (badlogic). TypeScript+Rust (~55k LoC nativas), runtime Bun; autor (can1357) com histórico consolidado em engenharia de baixo nível. Não instalado nesta missão (regra respeitada); análise por repositório e docs (`docs/`: context-files, compaction, handoff-generation-pipeline, advisor-watchdog, approval-mode, custom-tools, extensions...).

**Capacidades x valor para o Poseidon** (matriz completa no CSV; resumo por prioridade):

| Capacidade | Reduz | Decisão para o Poseidon | Prioridade |
|---|---|---|---|
| Hashline (edit por hash de conteúdo + rejeição de patch stale) | Falso-DONE, loops de edição, tokens (−61% em modelo testado) | **Reimplementar no executor .NET** (conceito simples: âncora = hash da linha/trecho; divergiu -> rejeita) | P0 |
| LSP acoplado (diagnostics/refs/rename) | Falso-DONE, retrabalho | **Sidecar/adapter**: consumir LSP servers direto do Runner é caro; o caminho barato é o item RPC abaixo | P1 |
| DAP (breakpoints, stepping, variáveis) | Tempo de debug | **Sidecar via omp RPC** | P1 |
| `omp --mode rpc` (NDJSON/stdio) | Integração | **Adapter `OmpRpcAgentExecutor`** — um executor que entrega LSP+DAP+hashline+subagentes de uma vez | P0/P1 |
| Advisor (segundo modelo observando cada turno) | Erros que o executor atropela | **Adaptar conceito** — no Poseidon já existe critic pós-tarefa; advisor é critic EM-turno; avaliar custo/cota | P2 |
| Typed subagent output (schema validado) | Handoffs frágeis | **Já é decisão do produto**; confirmar aplicação em todos os handoffs | P0 (verificação) |
| Checkpoint/rewind de contexto | Custo/poluição de contexto | **Inspiração** — equivalente Poseidon: rotação de sessão + digest | P2 |
| Hindsight (retain/recall/reflect por projeto) | Erros repetidos | **Inspiração** para o pipeline de aprendizado do doc 04 (com gate de promoção, que o omp não impõe) | P1 |
| Stream rules (injeção contextual sob desvio) | Custo de regra permanente | **Inspiração** — regras dormentes que só entram quando o padrão dispara | P2 |
| Review por severidade P0–P3 + veredito | Revisão vaga | **Adaptar formato** no critic do produto | P1 |
| Atomic commit splitting, conflict://, PR-as-filesystem | UX de Git | Inspiração / futuro | P3 |
| Round-robin de credenciais | — | **Rejeitar para multiusuário**; legítimo apenas para múltiplas chaves do MESMO titular com backoff (distinção da missão: credenciais autorizadas ≠ evasão de cota) | — |

**Comparação cruzada:** ECC ataca o problema com DOCUMENTOS (governança comportamental via texto); omp ataca com FERRAMENTAS (verificação mecânica no loop). A pesquisa da Etapa 2 indica que enforcement mecânico domina texto em aderência — logo, a ordem de valor para o Poseidon é: ferramentas do omp (via executor/adapter) > peças documentais do ECC (poucas, adaptadas) > qualquer importação estrutural.
