# Runtime de governança de agentes

Owner: Agent Platform. Versão: 1.0.0. Última verificação: 2026-07-20.

## Bundle e receipt

Todo turno adquirido pelo Chief monta contexto determinístico na ordem: núcleo, organização,
projeto, workflow/fase, persona, skills, constraints de path, StatusDigest, critérios, tools,
evidências, stop conditions e budget. Núcleo, critérios, stop conditions e budget nunca são
truncados. Conflito canônico ou segredo produz fallback mínimo, receipt e bloqueio antes do actor.
O cache é tenant/project scoped e indexado pelo checksum do bundle.

O receipt dual-provider registra IDs de turno/tarefa/tentativa/agente, versão do manifest,
documentos e checksums, razões/load policies, tokens estimados e reais quando disponíveis,
truncamentos, conflitos, cache hits, provider/model, timestamp, checksum, estado, gate e OCC. Eventos
separam `selected`, `delivered`, `opened_by_tool`, `rule_triggered`, `violation_detected`,
`item_truncated`, `gate_result`, patch e evaluator; texto de resposta nunca prova uso sozinho.

## Evaluator, patch e stale docs

O evaluator recebe somente critérios, diff, evidências e testes; não recebe sessão do actor nem
ferramenta de escrita. A saída 1.0.0 usa findings P0–P3, confidence, evidence, path/range, ruleId,
ação e verdict. Riscos medium/high/critical exigem identidade distinta e Default-FAIL.

Hashline compara `expectedChecksum` antes da escrita, rejeita stale sem best effort, sugere
`re-read/replan`, grava atomicamente e anexa evento sanitizado ao receipt. O detector de stale docs
gera findings/tarefas para review vencido, mudança de fonte, drift, enforcement inativo, adapter,
uso/truncamento e fontes concorrentes; nunca exclui documento.

O endpoint de patch exige sessão tenant-scoped, projeto/receipt coincidentes, repositório local
dentro do controlled root, identidade de agente resolvida e path permitido pela policy G3. Arquivos
compartilhados exigem ainda claim persistido e ativo da tentativa. A flag desabilitada restaura a
semântica de edição anterior, mas preserva confinamento, receipt e auditoria.

## Contratos e flags

OpenAPI expõe `/api/v1/governance-runtime`: receipts, métricas, evaluations, stale findings,
hashline, benchmark e catálogo de executores. As flags de rollback são:

- `Harness:Governance:Features:ContextBundlesEnabled` — fallback mínimo quando `false`;
- `Harness:Governance:Features:HashlinePatchesEnabled` — volta à edição anterior quando `false`;
- `Harness:Governance:Features:StaleDocumentDetectorEnabled`;
- `Harness:Governance:Evaluator:Enabled` e provider/model configuráveis;
- `Harness:AgentExecutors:OmpRpc:Enabled` e `Harness:IsolatedExecution:ExecutorId`.

Receipts não possuem flag de desligamento porque são requisito de auditabilidade do Chief. O
rollback das demais funções preserva receipt, claims, auditoria e gates autoritativos.

## Recuperação e operação

As migrations `0045_governance_runtime.sql` são separadas para SQLite e PostgreSQL. O create do
receipt é idempotente por tenant/turn e rejeita checksum divergente; transições usam OCC. Reinício
entre seleção e completion é coberto por teste de recovery. O Host publicado inclui manifest e
documentos selecionáveis, portanto o bundle não depende da árvore fonte do desenvolvedor.

Falha de leitura/validação do manifest gera receipt `0.0.0`, fallback obrigatório e conflito tipado;
o actor não é chamado. Falha do próprio store continua sendo indisponibilidade autoritativa e
impede o turno. Actual prompt tokens permanece nulo quando o provider não informa o valor.
