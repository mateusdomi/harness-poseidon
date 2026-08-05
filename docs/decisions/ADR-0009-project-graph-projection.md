# ADR-0009 — ProjectGraphProjection: um grafo que é projeção, nunca fonte da verdade

## Contexto

A perícia do run de empréstimos (Fase 6D) mostrou o padrão que mais custou caro: decisões tomadas
sobre estado que ninguém conseguia ENUNCIAR. O ADR-0005 amputou o escopo do produto e nada marcou
o plano de testes, o quality gate da fase 6 nem o parecer Go/No-Go como afetados; um card de code
review foi despachado antes dos cards que ele revisaria; um card cancelado continuou contando como
cobertura do requisito. Em todos os casos a informação EXISTIA nas fontes canônicas — cards,
demandas, decisões, evidência — mas não existia nenhuma estrutura que respondesse "o que depende
do quê" e "o que esta mudança invalida".

A missão Graph-Driven Project Intelligence (2026-08-05) constrói essa estrutura em quatro ondas.
Esta é a fundação (Onda 1).

## Decisão

1. **O grafo é PROJEÇÃO derivada** das fontes canônicas — nunca fonte da verdade. Pode ser
   apagado e reconstruído (`poseidon graph rebuild <projectId>` /
   `POST /api/v1/projects/{id}/graph/rebuild`) a qualquer momento, e a reconstrução produz grafo
   IDÊNTICO ao incremental. A equivalência é de construção (ids determinísticos derivados de
   tipo+fonte, ordenação total, evento que carrega o estado completo do item) e está congelada em
   teste (`ProjectGraphProjectorTests.RebuildEIncrementalProduzemGrafosIdenticos`).

2. **Conjuntos FECHADOS.** 13 tipos de nó (`Requirement, Nfr, HumanFact, Constraint, Decision,
   Risk, OpenQuestion, Artifact, Phase, Gate, Card, Test, Evidence`) e 12 tipos de aresta
   (`implements, verifies, proves, derives_from, constrained_by, impacts, depends_on, blocked_by,
   supersedes, invalidates, requires, produces`). **Não existe aresta genérica** (`relates_to` ou
   equivalente): o enum não a expressa, o CHECK do banco a recusa e o teste de arquitetura a
   caça pelo nome. Aresta genérica é como um grafo morre — tudo conectado a tudo, nada explicando
   nada.

3. **Proveniência com consequência.** Aresta estrutural (fato canônico: card implementa demanda,
   evidência prova card) é `Deterministic / 1.0 / Accepted` — imposto pela fábrica
   `GraphEdge.Structural` E pelo CHECK do banco. Aresta inferida por modelo nasce
   `ModelInference / Proposed` com confiança declarada e NUNCA bloqueia readiness nem dispara
   invalidação — informa o digest até ser promovida por regra determinística ou decisão humana.

4. **STALE é estado operacional, não derivado.** Mudança upstream não apaga nem reverte nada:
   marca o nó com a CAUSA (nó + versão). Um rebuild preserva o STALE de nós cuja fonte não mudou
   de versão; quando a fonte avança, a própria mudança era a revalidação que o STALE esperava, e
   a marca cai. Nó aposentado (card cancelado) fica `Retired` com as arestas preservadas — a
   história de por que algo dependia de algo é exatamente o que a perícia não tinha. E um card
   cancelado NÃO emite `implements`: cobertura de requisito por card morto foi um dos defeitos
   centrais do run real.

5. **Flag `graph.projection.enabled`** (config `Harness:Graph:ProjectionEnabled`), default
   **off** até a Onda 4 aprovar as provas de replay. Desligada, os endpoints respondem 409 com
   código estável (`graph_projection_disabled`) — desligado é estado declarado, não 404.

6. **Cobertura da ligação Host→fontes nesta onda** (declarada): solicitações (Artifact),
   demandas (Requirement), cards (Card), fases derivadas dos cards (Phase), tentativas aprovadas
   (Evidence). Decisões, riscos, NFRs, fatos humanos, restrições, perguntas abertas, gates e
   testes já têm tipo e invariantes no modelo; entram na ligação conforme cada fonte canônica
   expõe leitura estruturada (Ondas 2–4 ampliam pelo caminho do replay).

## Exclusões conscientes (o que NÃO entra nesta missão)

- **Neo4j ou qualquer graph database.** As tabelas relacionais existentes (SQLite/Postgres, com
  RLS) atendem o volume de um projeto e herdam backup, migração e tenancy de graça.
- **Visualização de grafo no front.** A Bruna consome digest e consultas; o dono consome
  respostas dela.
- **Code Graph.** Permanece 0 nós até existir código de produto para indexar.
- **GraphPlanner / priorização automática.** O grafo INFORMA readiness e digest; não decide
  backlog.
- **Planned × Realized.** Uma única projeção do estado real; comparação plano-vs-realizado é
  missão futura.

## Consequências

- Migração 0131 (`project_graph_nodes`, `project_graph_edges`, `project_graph_snapshots` com
  versão monotônica por projeto e motivo por aplicação). Tripwires: 107 SQLite / 108 Postgres.
- Provas: invariantes de fábrica e de conjunto fechado, idempotência, equivalência
  rebuild≡incremental (unit); roundtrip fiel, versão monotônica, remoção do que sumiu, semântica
  STALE sob rebuild (recovery, banco real).
- As Ondas 2 (impacto + readiness), 3 (digest + consultas da Bruna) e 4 (provas de replay do run
  de empréstimos) constroem sobre esta fundação; a flag só liga quando a Onda 4 aprovar.
