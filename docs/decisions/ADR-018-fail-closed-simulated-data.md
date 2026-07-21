# ADR-018 — Dados simulados nunca apresentados como reais (fail-closed)

- Status: aceito
- Data: 2026-07-21

## Contexto

Na RC3 o catálogo de providers era semeado por um `EnsureAsync` chamado no topo
de quase toda leitura/escrita dos stores SQLite e PostgreSQL. Ele inseria, para
todo tenant, uma conta "OpenAI account" ativa, modelos "GPT-5"/"GPT-5 Codex"/
"Claude Sonnet" habilitados e orçamentos de US$ 100/US$ 200 — via
`INSERT OR IGNORE`/`ON CONFLICT DO NOTHING`. Em uma instalação vazia o produto
exibia essas contas, modelos e cotas como se o usuário as tivesse configurado, e
o `ChiefInvocationRoutingService` — que valida corretamente modelo habilitado +
conta ativa + effort mapeado + cota — passava com base nesses dados fictícios.
O `FakeAgentExecutor`, ligado incondicionalmente, então respondia com um texto
fixo ("O turno foi registrado de forma durável…") apresentado como resposta do
Chief (ver ADR-019 para a separação transporte × resposta).

## Decisão

### 1. Pacote de homologação normal é fail-closed

Uma instalação vazia não contém nenhuma conta, modelo, cota, política de
roteamento ou definição habilitada que o usuário não tenha configurado.
Concretamente:

- `EnsureAsync` deixa de semear contas, modelos, orçamentos e roteamento. Ele
  passa a semear **somente os tipos de provider conectáveis** (OpenAI, Anthropic,
  Ollama) como catálogo estático de tipos suportados — sem conta, sem modelo, sem
  cota e sem saúde "healthy". Um tipo de provider listado sem conta não é um
  provider "saudável"; é um ponto de entrada para "Conectar".
- As definições built-in de agentes deixam de referenciar um `default_model_id`
  fictício: passam a `NULL` (não resolvido até o usuário configurar), com
  prontidão explícita via ADR-017 (`ModelReady = Unconfigured`).

### 2. Modo simulado é explícito, marcado e isolado

O modo simulado só existe sob configuração explícita de desenvolvimento/demo,
reutilizando a flag já existente `Harness:Demo:Enabled` (que hoje semeia perfil/
organização/projeto de homologação). Sob demo:

- o seed de conta/modelo/orçamento/roteamento é aplicado **apenas ao tenant de
  demo**, por um seed dedicado e idempotente, nunca pelo caminho de leitura dos
  stores;
- todo dado simulado é marcado (`executionMode=simulated` no read model de
  prontidão) para que a UI o identifique visualmente;
- nenhum artefato simulado aparece no pacote de homologação normal.

### 3. Estado de configuração fechado

Toda dependência usa o enum de ADR-017
(`Unconfigured|Simulated|Configured|Ready|Degraded|Unavailable`). `Simulated` é
inatingível sem a flag de demo.

## Consequências

- Instalação vazia mostra o golden path honesto: nada "pronto" até configurar.
- Testes que dependiam do seed passam a semear seus próprios dados via um helper
  de teste compartilhado (`ProviderCatalogTestSeed`), mantendo o comportamento
  determinístico sem acoplar produção a dados fictícios. Esta é a maior fatia de
  migração de testes desta decisão e é executada junto com a remoção do seed.
- O `ChiefInvocationRoutingService` deixa de passar por engano: sem conta/modelo
  reais ele bloqueia, e o bloqueio é apresentado como estado tipado (ADR-019),
  não como resposta simulada.
- Rollback: a flag de demo restaura integralmente o comportamento anterior para
  fins de demonstração, sem reintroduzir dados fictícios no pacote normal.
