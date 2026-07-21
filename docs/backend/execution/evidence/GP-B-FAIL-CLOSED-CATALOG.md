# GP-B — instalação vazia deixa de apresentar catálogo simulado como real

Data: 2026-07-21. Branch: `develop`.

Fatia B do fechamento do golden path (ADR-018). Elimina o estado em que uma instalação
vazia exibia conta "OpenAI account", modelos "GPT-5"/"GPT-5 Codex"/"Claude Sonnet" e
orçamentos de US$ 100/US$ 200 como se o usuário os tivesse configurado.

## Causa raiz

O dado fictício não estava em migration: o método `EnsureAsync` de
`SqliteProviderCatalogStore` e `PostgresProviderCatalogStore` era chamado no topo de quase
toda leitura/escrita do catálogo e reinseria contas, modelos, roteamento e orçamentos por
`INSERT OR IGNORE` / `ON CONFLICT DO NOTHING`. Remover apenas os literais seria inócuo: a
própria chamada de seed no caminho de leitura é o defeito. Como o
`ChiefInvocationRoutingService` valida modelo habilitado + conta ativa + effort mapeado +
cota, ele passava com base nesses dados fictícios — a fabricação de dados e a falsa
prontidão se reforçavam.

## Entrega

- `EnsureAsync` passa a semear **somente os tipos de provider conectáveis** (OpenAI,
  Anthropic, Ollama). Um tipo listado sem conta não é provider saudável: é o ponto de
  entrada para "Conectar". Nenhuma conta, modelo, cota ou roteamento é criado.
- Todo o catálogo simulado migrou para `IProviderCatalogStore.SeedSimulatedCatalogAsync`,
  idempotente e explícito, implementado nos dois providers.
- `DemoDataHostedService` — registrado apenas sob `Harness:Demo:Enabled` — é o único
  caminho de produção que invoca esse seed. Ele executa depois do perfil (que provisiona o
  tenant referenciado pelas FKs) e antes do projeto, para que o Chief da demo nasça com
  modelo resolvível.
- Migration dual `0047_unconfigured_default_models` limpa para NULL as referências
  fictícias de `default_model_id` das definições built-in: provider/account/model ficam
  não resolvidos até o usuário configurar, com prontidão explícita (ADR-017). Não há FK
  sobre a coluna; a limpeza é idempotente e restrita aos IDs do seed da RC3.
- Fixtures que precisam de catálogo pedem-no deliberadamente por
  `ProviderCatalogTestSeed`, sempre rotulado como simulado, em vez de depender de um seed
  automático de produção.

## Prova

Novo gate `EmptyInstallFailClosedTests`: um Host real, sem modo demo, com perfil,
organização e projeto criados por HTTP, comprova que

- `/api/v1/accounts`, `/models`, `/budgets` e `/routing-policies` retornam vazio;
- as respostas não contêm `gpt-5`, `OpenAI account` nem `Claude Sonnet`;
- os tipos de provider conectáveis continuam listados como ponto de entrada;
- a prontidão retorna `ProviderAccountReady=Unconfigured` com bloqueador
  `provider_account.missing` e ação `provider.connectAccount`, `ModelReady=Unconfigured`,
  `ExecutionReady` diferente de `Ready` com bloqueadores, e `overallState` não `Ready`;
- as etapas dependentes de configuração externa reportam `executionMode=unconfigured` —
  nem `real`, nem `simulated`, já que o modo demo está desligado.

`CatalogStoreBehavior` (cenário provider-neutro usado por SQLite e PostgreSQL) passou a
asserir explicitamente que contas, modelos e orçamentos estão **vazios antes** de pedir o
catálogo simulado, transformando o comportamento dual-provider em guarda permanente do
ADR-018.

## Gates executados

- `dotnet build Harness.sln -c Release`: 0 avisos, 0 erros.
- `dotnet format --verify-no-changes`: limpo.
- Suíte integral: **318** testes verdes — 177 unit, 94 integration (inclui o novo gate),
  31 contract, 7 architecture, 6 recovery, 3 concurrency. PostgreSQL e Docker exercidos
  pelos testes correspondentes.
- Governança: `sync` até `changed=0`, `generate`, `lint --warnings-as-errors` verde.
- `tools/backend/scan-secrets.sh`: limpo.

Nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi modificado.

## Efeito no golden path

A instalação vazia passa a ser honesta: nada aparece pronto antes de o usuário configurar.
O Chief deixa de ter modelo/conta resolvíveis por dado fictício, portanto a invocação
falha fechada — o que torna a Fatia C (separar confirmação de transporte da resposta real
e devolver estado bloqueado tipado em vez de resposta simulada) o próximo passo necessário
para completar a jornada.
