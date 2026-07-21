# Golden path — handoff backend → frontend (rodada 2026-07-21)

Owner: Backend/Produto. Público: agente frontend (Kimi) e homologação.
Fonte normativa: ADR-017, ADR-018, ADR-019, ADR-020 e `governance/core.md`.

Este documento publica os contratos e eventos **primeiro** e descreve o handoff.
A nova Release Candidate só é gerada após o commit frontend desta rodada
integrar os tipos TypeScript correspondentes. Nunca há merge em `main`, force
push ou GitHub Release.

## 1. Objetivo da rodada

Fechar o golden path honesto — do install vazio à primeira execução real do
Chief — eliminando estados que apresentam contas/modelos/cotas/respostas
simuladas como reais. O eixo é o **read model de prontidão** (ADR-017) e o
comportamento **fail-closed** (ADR-018) com **separação transporte × resposta**
(ADR-019).

## 2. Contrato de prontidão (ADR-017)

`GET /api/v1/projects/{projectId}/readiness` → `ProjectReadinessSnapshot`.

Forma canônica (campos estáveis; nomes em `camelCase` no JSON):

```jsonc
{
  "projectId": "01J…",            // pode ser null antes de existir projeto
  "overallState": "Unconfigured", // menor estado na ordem canônica
  "steps": [
    {
      "step": "ProviderAccountReady",   // enum ReadinessStep, ordem canônica
      "state": "Unconfigured",          // enum ConfigurationState
      "executionMode": "unconfigured",  // unconfigured | simulated | real
      "capability": "providers.accounts",
      "messageCode": "readiness.providerAccount.unconfigured", // código, não texto
      "relatedIds": [],
      "blockers": [
        { "code": "provider_account.missing", "relatedIds": [] }
      ],
      "nextAction": {
        "code": "provider.connectAccount",
        "route": "/providers",
        "resourceId": null
      }
    }
    // … uma entrada por etapa
  ],
  "nextActions": [                 // deduplicadas, na ordem das etapas bloqueadas
    { "code": "provider.connectAccount", "route": "/providers", "resourceId": null }
  ]
}
```

### Enums fechados

`ConfigurationState`: `Unconfigured | Simulated | Configured | Ready | Degraded | Unavailable`.

`ReadinessStep` (ordem canônica):
`ProfileReady, OrganizationReady, ProjectReady, ProviderAccountReady, ModelReady,
WorkflowReady, ChiefDefinitionReady, AgentPoolReady, ExecutionReady`.

`executionMode`: `unconfigured | simulated | real`.

### Regras de cálculo (somente leitura, sem persistência própria)

- `ProfileReady`: existe perfil local / sessão.
- `OrganizationReady`: existe ≥ 1 organização no tenant.
- `ProjectReady`: existe o projeto e ele pertence a uma organização.
- `ProviderAccountReady`: existe conta `state=active`. `Simulated` se a conta é
  do tenant de demo (flag `Harness:Demo:Enabled`); `Unconfigured` se nenhuma.
- `ModelReady`: existe modelo habilitado com capability `chat` e `effort`
  mapeado, vinculado a uma conta ativa. `Unconfigured` se nenhum.
- `WorkflowReady`: projeto tem workflow publicado vinculado; `Unconfigured` se
  não (projeto pode existir sem workflow, mas não parece pronto para execução).
- `ChiefDefinitionReady`: definição Chief existe e resolve model+account
  (via `ChiefInvocationRoutingService`); `Unconfigured`/`Degraded` conforme.
- `AgentPoolReady`: instância Chief existe e está `idle/ready`.
- `ExecutionReady`: `Ready` só quando todas as anteriores `Ready|Configured` e
  há binding verificável provider+model+workflow.

### Evento

`readiness.changed` no stream do projeto: `{ projectId, overallState, changedSteps: [ { step, state } ] }`. Payload sanitizado, sem segredo.

## 3. Eventos publicados primeiro (ADR-019)

Adicionados ao `EventTypeCatalog` e a `docs/contracts/events.json` nesta rodada
(declarados; emissão nas transições entra com as fatias de comportamento):

| Evento | Stream | Payload (sanitizado) |
|---|---|---|
| `readiness.changed` | projeto | `{ projectId, overallState, changedSteps[] }` |
| `message.received` | conversa | `{ conversationId, messageId }` |
| `turn.registered` | conversa | `{ turnId, conversationId }` (ACK de transporte, **não** resposta) |
| `execution.enqueued` | projeto | `{ turnId, projectId }` |
| `provider.invoked` | projeto | `{ turnId, providerId, modelId, effort }` |
| `model.responded` | conversa | `{ turnId, messageId }` |
| `execution.blocked` | conversa | `{ turnId?, conversationId, blockers[], nextActions[] }` |

Já existentes e reutilizados: `chat.turnStarted/turnChunk/turnCompleted`,
`chief.turnStateChanged` (passa a ser emitido), `message.appended`,
`demand.created`.

## 3.1 Turno bloqueado tipado (C2 — **MUDANÇA DE CONTRATO**)

`POST /api/v1/conversations/{conversationId}/turns` **não retorna mais `400`** quando falta
provider/modelo/workflow. A mensagem humana é persistida e o turno volta `202` com estado
tipado:

```jsonc
{
  "turnId": "01J…",
  "conversationId": "01J…",
  "state": "blocked",          // pending | processing | completed | failed | blocked
  "correlationId": "turn:01J…",
  "readiness": { "overallState": "Unconfigured", "executionState": "Unconfigured" },
  "blockers": [ { "code": "provider_account.missing", "relatedIds": [] } ],
  "nextActions": [ { "code": "provider.connectAccount", "route": "/providers", "resourceId": null } ],
  "links": {
    "readiness": "/api/v1/projects/{projectId}/readiness",
    "conversation": "/api/v1/conversations/{conversationId}"
  }
}
```

- `state=pending` significa registrado e enfileirado para execução real.
- `400` fica reservado a request estruturalmente inválido; `409` a conflito de conversa.
- O bloqueio é durável e auditável (`chief_turn_blocks`, ledger e Outbox) e emite
  `execution.blocked`.
- Idempotente por mensagem: o retry do mesmo envio devolve o mesmo bloqueio, sem duplicar
  mensagem, turno ou evento.
- **Pré-requisito novo:** o gate do turno é `ExecutionReady`, que inclui `WorkflowReady`.
  Um projeto sem workflow vinculado bloqueia com `workflow.unbound`.

**Ação do frontend:** ler `state`/`blockers`/`nextActions` em vez de tratar `400` como
bloqueio (ver `docs/frontend/HANDOFF_API.md` §9.1.1, que documentava o comportamento antigo).

## 3.2 Payloads dos eventos publicados (C3)

`docs/contracts/events.json` subiu para **1.2** e ganhou a seção `payloads`, com schema por
evento (campos obrigatórios e tipos). Atende ao pedido de `docs/frontend/HANDOFF_API.md`
§9.1.2: os schemas permissivos podem virar tipados.

Emissão real por transição:

| Transição | Eventos |
|---|---|
| Enfileiramento | `message.received`, `turn.registered`, `execution.enqueued`, `chief.turnStateChanged` (`pending`) |
| Recusa por prontidão | `execution.blocked` |
| Aquisição do lease | `provider.invoked`, `chief.turnStateChanged` (`processing`) |
| Conclusão | `chat.turnStarted/Chunk/Completed`, `message.appended`, `model.responded`, `chief.turnStateChanged` (`completed`), `demand.created` |
| Falha/retry | `chief.turnStateChanged` (`failed`/`pending`, com `errorCode`) |

`model.responded` é a resposta do MODELO; `chat.turnCompleted` é conclusão de TRANSPORTE.
`chief.turnStateChanged` agora é emitido (antes só declarado) e carrega `state` do conjunto
fechado `pending|processing|completed|failed|blocked`. Payloads são sanitizados: apenas
identificadores e seleção, nunca conteúdo de prompt/resposta ou segredo.

## 4. O que o frontend deve publicar (habilita a metade frontend do drift)

Os testes de drift de contrato (`tests/Harness.ContractTests/**`) asseguram que
o OpenAPI backend e os tipos TS do frontend concordam. Esta rodada assere
**apenas o lado backend** dos novos contratos. Para reconciliar, o commit
frontend desta rodada deve adicionar:

1. `frontend/src/api/contracts/*.ts`: um `readinessSnapshotSchema` (ou nome
   equivalente) com os campos de `ProjectReadinessSnapshot` e os enums
   `ConfigurationState`, `ReadinessStep`, `executionMode` acima.
2. `frontend/src/api/contracts/events.ts`: os novos tipos de evento da seção 3.
3. Catálogo de tradução de `messageCode`/`blocker.code`/`nextAction.code` →
   texto localizado (pt-BR), pois o domínio nunca emite texto livre.
4. Consumir `readiness` para: bloquear "criar projeto" sem organização (deep link
   de retorno), bloquear input de chat sem prontidão (blockers tipados), e
   exibir `executionMode=simulated` com identificação visual.
5. Remover do golden path o controle "criatividade" (ADR-020) enquanto não houver
   binding backend.

Após esse commit frontend, o backend habilita a metade frontend dos testes de
drift e só então gera a nova RC.

## 5. Decisão de produto — plano por licença (S5.2)

Registrada aqui como decisão de produto (ADR dedicado é backlog):

- O plano **não** é escolhido livremente pelo usuário comum; vem da
  licença/tenant/administração comercial. Modo pessoal deriva plano/capabilities
  da licença local.
- Enquanto a monetização não existir, o backend retorna **um plano técnico único
  e documentado** (sem Free/Pro/Enterprise enganoso) e um catálogo de
  capabilities por plano que a UI usa para explicar diferenças.
- Nenhum valor de plano fictício é apresentado como real.

Slug de organização (S5.1): gerado automaticamente do nome, único, lowercase +
hífen + limite de tamanho; edição é campo avançado; o contrato mantém `slug`, e a
UI recebe metadata "Identificador da URL". Branding (S5.3) é opcional com defaults
do Poseidon, nunca blocker do golden path.

## 6. Fatias de comportamento (staged, próximos commits em `develop`)

1. **Fatia A — Prontidão (esta rodada):** contrato + read model + endpoint +
   eventos declarados + testes backend + ADRs + governança. Aditivo, verde.
2. **Fatia B — Fail-closed (ADR-018):** remover seed de conta/modelo/orçamento/
   roteamento do `EnsureAsync`; manter só tipos de provider conectáveis; seed de
   demo isolado sob `Harness:Demo:Enabled`; `default_model_id` das definições
   built-in → NULL. Migração de testes dependentes do seed para
   `ProviderCatalogTestSeed`. Readiness passa a reportar `Unconfigured`
   corretamente.
3. **Fatia C — Chief real × transporte (ADR-019):** ligar `FakeAgentExecutor`
   só sob modo simulado; endpoint de turno retorna estado bloqueado tipado sem
   fingir resposta quando não há Chief real; emitir eventos de ciclo de vida e
   `chief.turnStateChanged`; primeira conversa idempotente.
4. **Gate do golden path (S15):** E2E de pacote sem demo com asserts fail-closed.

Cada fatia entra como commit verde próprio; nenhuma RC antes do commit frontend.

## 7. Itens de backlog (secundários ao golden path, ADRs próprios)

Seed completo das 6 definições built-in (persona/missão/responsabilidades/…,
coluna `owner`); auto-slug de `agent_key` a partir do nome; catálogos de
team/specialty (hoje colunas livres); workflow selecionado na criação do projeto
com template recomendado e "pular com aviso"; import/export de definição de
agente (JSON/YAML) e "Gerar com o Chief" (draft, revisão humana); metadata rica
de atividade recente com catálogo de tradução versionado; effort — verificar
teste de contrato e superfície "indisponível quando não suportado" (binding já é
capability-aware).
