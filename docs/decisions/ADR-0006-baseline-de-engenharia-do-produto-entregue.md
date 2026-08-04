# ADR-0006 — Baseline de engenharia do produto entregue

## Contexto

O primeiro teste real da fábrica pediu um "sistema de empréstimos". O que foi
produzido respondia `GET /emprestimos` com `{"emprestimos": []}` e não tinha
interface. A justificativa registrada foi que o usuário não havia informado qual
stack usar.

A auditoria do canon confirma que a justificativa é consequência de uma lacuna real,
não de desobediência do modelo. Verificado documento a documento:

- `governance/core.md`, `governance/rules/*` e `docs/contracts/*` regem **como agentes
  trabalham no repositório do Poseidon**: card, revisão independente, gates
  Default-FAIL, segredos, branches `main`/`develop`, paridade SQLite/PostgreSQL,
  `tools/backend/verify.sh`. Todos têm `scope: repo`.
- Uma varredura por `SQL Server`, `React`, `Vite`, `Clean Architecture`, `.NET 8`,
  `Swagger`, `OpenAPI` e `stack padrão` em `governance/` e em `docs/` (excluindo
  auditorias) retorna **duas ocorrências**, ambas referências ao arquivo de contrato
  `docs/contracts/openapi.json` do próprio Poseidon.
- Não existia nenhuma norma sobre o **software que o Poseidon constrói**: nem stack
  default, nem obrigação de interface, nem padrões .NET/C# para o código gerado, nem
  Definition of Done de produto.

Além disso, o gate da Fase 3 do playbook cobra literalmente *"aderência ao constraint
profile (desvio exige ADR)"* — e o `constraint profile` **não estava definido em lugar
nenhum**: nem documento, nem schema, nem tipo em código. O gate exigia aderência a um
artefato inexistente.

Sem norma, o executor preencheu a lacuna com juízo próprio. Foi exatamente o modo de
falha que `governance/core.md` já nomeia: *"uma capacidade cujo caminho dependa de o
modelo lembrar de segui-lo não é uma garantia — é uma esperança com nome de regra"*.

## Decisão

**O Poseidon passa a ter um canon de segundo escopo — `product` — que rege o software
entregue ao usuário, distinto e simultâneo ao canon `repo` que rege o próprio
repositório.**

1. O canon do produto vive em `docs/product/`, registrado no
   `governance/manifest.yaml` com `scope: product`. Ele **não substitui** nenhuma
   regra de `governance/`: os dois escopos valem ao mesmo tempo, sobre objetos
   diferentes.

2. **Ausência de especificação do usuário é aplicação do baseline, nunca omissão de
   parte do produto.** Um pedido em prosa que designe algo operado por uma pessoa
   ("sistema", "aplicação", "portal", "gestão", "cadastro", "controle") tem como
   modalidade default um produto web utilizável — banco, backend, API documentada e
   **interface**.

3. A stack default, quando o usuário não escolhe, é .NET 8 / ASP.NET Core 8 no
   backend, React + TypeScript + Vite no frontend, SQL Server como banco, monólito
   modular com Clean Architecture, OpenAPI obrigatório para toda API HTTP.

4. **Override explícito do usuário vence o default e não é revertido pelo agente.** A
   preferência individual do agente não é fonte válida de decisão em nenhum nível.

5. O **perfil efetivo do projeto** (a Parte 2 de `docs/product/baseline.md`) passa a ser a
   definição do `constraint profile` que o gate da Fase 3 já cobrava. Ele é produzido
   na Fase 3 e é entrada obrigatória das fases seguintes.

6. Um card cujo produto seja operado por pessoa **não pode ser concluído sem interface
   utilizável**, salvo escopo aprovado que diga o contrário. Build verde e `200` num
   endpoint não constituem entrega.

## Consequências

- **O default deixa de ser inventado por turno.** A decisão técnica passa a emergir de
  baseline + perfil efetivo + ADR, e não da lembrança do modelo.
- **O gate da Fase 3 ganha o referente que lhe faltava.** "Aderência ao constraint
  profile" passa a ser verificável.
- **O canon cresce em um escopo novo, não em duplicidade.** Nenhuma regra de
  `governance/` foi copiada para `docs/product/`; onde a responsabilidade já existia
  (contrato de card, documentação, coordenação, revisão), o documento existente foi
  complementado em vez de substituído.
- **Custo de contexto, dimensionado.** Seis documentos novos. Apenas dois entram no
  bundle de toda execução — `baseline.md` e `definition-of-done.md`, que carregam as
  regras que impedem a regressão dos empréstimos. Os quatro de disciplina (backend,
  frontend, dados, segurança e operabilidade) são `load: on-demand`, alcançados pela
  etapa de descoberta do protocolo de execução. A divisão não é estética: com todos em
  `bundle`, a soma medida era 11.955 tokens contra um orçamento de 8.000, e o
  truncamento evictava `rule-security`, `rule-secrets` e `security-threat-model` de
  toda execução. Trocar segurança por padrões de código seria um péssimo negócio.
- **A força normativa ainda depende de mecanismo.** Um documento canônico só governa se
  o `ContextBundleBuilder` o selecionar. Os seletores do novo canon foram declarados de
  forma abrangente por causa disso — ver "Riscos aceitos".

## Riscos aceitos

- **O vocabulário de seleção do manifest não coincide com o que o runtime passa.** Em
  uma execução de agente, `AgentRunOrchestrator` monta o `ContextBundleRequest` com
  `workflow: "agent-run"`, `phase: "execution"` e `taskType: <papel da conta>`,
  enquanto o manifest declara `workflows: [standard-nine-phase]`, fases do playbook
  (`triage`, `planning`, `development`) e tipos de card (`historia`, `tarefa`, `bug`).
  Documentos que discriminam por esses campos **nunca são selecionados** numa execução
  real. Medido: para um card com `paths=['src/**']` e papel `backend-specialist`, apenas
  11 dos 41 documentos do manifest eram selecionados; `rule-testing`, `rule-code-review`,
  `rule-git`, `contract-card`, `contract-handoff` e `workflow-standard` ficavam de fora
  por declararem tipos de card do playbook onde o runtime passa o papel da conta. Por
  isso o canon do produto usa `'*'` nos seletores universais e `pathGlobs: ['**']`: é a
  única declaração comprovadamente alcançável hoje. Corrigir o vocabulário é trabalho de
  implementação, registrado como lacuna e não resolvido aqui — os seletores dos
  documentos preexistentes não foram retocados, porque mexer neles mudaria o que os
  agentes atuais veem.
- **O orçamento de contexto está no limite.** Com o novo canon, o bundle documental de
  uma execução mede 7.996 tokens contra o default de 8.000 de
  `AgentRuns:ContextTokenBudget`. Em cards de papel `backend-specialist`, que carregam
  dois documentos a mais, `rule-coordination` é truncada — o corte fica registrado em
  `truncated` e no recibo de governança, não é silencioso. Elevar o orçamento para
  16.000 remove o aperto e permitiria promover os quatro documentos de disciplina a
  `bundle`. É mudança de configuração do operador, fora do escopo desta decisão
  documental.
- **A Bruna não recebe este canon no turno de conversa.** O caminho conversacional não
  usa o `ContextBundleBuilder`; ele monta o prompt com a persona compilada, a política
  de comunicação e o `governance/core.md`. O canon do produto alcança os **executores**,
  não a chefe, enquanto isso não mudar.

## Alternativas descartadas

- **Ajustar o prompt da Bruna para "lembrar de fazer frontend".** Rejeitado: é
  exatamente a esperança com nome de regra que o núcleo proíbe. Na primeira execução em
  que o modelo não lembrar, nada acusa.
- **Copiar o baseline inteiro para dentro de cada projeto gerado.** Rejeitado: cria
  divergência silenciosa — o baseline evolui e a cópia não. O projeto herda por
  referência e declara apenas os próprios overrides.
- **Estender `governance/rules/` com as regras de stack.** Rejeitado: `governance/`
  tem `scope: repo` e rege como agentes trabalham *neste* repositório. Misturar os dois
  escopos no mesmo documento tornaria ambíguo, por exemplo, se "banco padrão SQL
  Server" se aplica ao Poseidon (que usa SQLite e PostgreSQL) ou ao produto entregue.
- **Criar quinze documentos espelhando a proposta recebida.** Rejeitado: quatro das
  responsabilidades propostas (protocolo de execução, contrato de card, governança
  documental e telemetria de contexto) já tinham dono canônico no repositório —
  `governance/rules/coordination.md`, `docs/contracts/card.md`,
  `governance/rules/documentation.md` e os recibos de governança já persistidos. Foram
  complementadas nos documentos existentes. Stack padrão e perfil efetivo também foram
  fundidos num só documento, porque default global e resolução local do default são o
  mesmo assunto em duas metades. Número de arquivos não é indicador de qualidade
  documental.

## Relacionado

- [`docs/product/baseline.md`](../product/baseline.md) — a stack default, a regra de override e o constraint profile.
- [`docs/product/definition-of-done.md`](../product/definition-of-done.md) — a regra de bloqueio por ausência de interface.
- [`docs/architecture/workflows/standard-workflow.md`](../architecture/workflows/standard-workflow.md) — a Fase 3 que produz o perfil.
- [`docs/contracts/card.md`](../contracts/card.md) — os campos que levam o perfil até o executor.
- [`governance/core.md`](../../governance/core.md) — a precedência normativa e a distinção de escopos.
