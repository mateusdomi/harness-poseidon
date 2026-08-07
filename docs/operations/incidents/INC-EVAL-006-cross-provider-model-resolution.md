# INC-EVAL-006 — modelo Anthropic ("opus") atribuído a uma conta Codex/OpenAI

**Data:** 2026-08-07 12:04 UTC · **Projeto:** Indicadores TrensRJ ·
**Card:** `01KZAVEAT5BMK29ME6FBCH2C7H` (FEAT/T02 Frontend: Hierarquia configurável) ·
**Conta:** `worker-codex-frontend` (provider `codex`/OpenAI) ·
**Detecção:** supervisão hands-off, na primeira janela de dispatch real pós-restart da
recuperação de throughput da Fase 5.

## Sintoma

A primeira tentativa despachada para `worker-codex-frontend` depois do restart falhou em
sub-segundo. `model_invocations` registra `model=opus`, `requested_model=01ARZ3NDEKTSV4RRFFQ69G5FJ6`
(o `model_id` interno que resolve para "opus"), `outcome=authenticationrequired`,
`usage_unknown`. `work_attempts.failure_reason='executor.account_model_unsupported'`. O ledger de
disponibilidade da conta registrou corretamente:

```
State: AuthenticationRequired
ReasonCode: run.account_model_unsupported
ConsecutiveFailures: 1
```

**A classificação está certa** (INC-EVAL-001, corrigido nesta mesma sessão, evita que isto vire
`account.quota_limited`) — mas a causa de fundo continua aberta: uma conta cujo provider é
Codex/OpenAI recebeu como argumento de modelo um identificador que só existe no vocabulário da
Anthropic. É semanticamente impossível a CLI da Codex aceitar `opus`.

## Causa

`worker-codex-frontend` herdou `model_id` de um `agent_definitions`/`agents` cujo `default_model_id`
(ou fallback resolvido no roteamento) aponta para um modelo Anthropic. A resolução de
model/effort hoje é feita majoritariamente por EXECUTOR (`ExecutorCatalog.SupportsEffort`) e por
persona/role — nada na cadeia de resolução impede que uma conta de um provider receba o
identificador de modelo de outro provider. Ver INC-EVAL-001 para o mesmo eixo estrutural (capability
por conta, não só por executor) — este incidente é a manifestação mais grave dessa mesma lacuna:
não é "efforte não suportado por esta conta", é "modelo de outro fabricante inteiro".

## Contorno

Nenhum aplicado ainda — 1 ocorrência isolada, não repetida. `worker-codex-frontend` segue
elegível para dispatch; não foi retirado da fleet.

## Gatilho decidido para ação (2026-08-07, decisão do dono)

- 1ª ocorrência: registrar este incidente, **não pausar os projetos**, não corrigir o
  subsistema de resolução de modelo durante a janela de observação em curso.
- 2ª ocorrência equivalente (mesma conta, mesma família de erro
  `account_model_unsupported`/modelo de outro provider): retirar **somente essa conta** da
  elegibilidade de dispatch temporariamente, pelo mecanismo seguro já existente (estado da
  conta no ledger de disponibilidade), para não repetir o padrão
  "selecionada → falha imediata → selecionada → falha imediata" que a fábrica está tentando
  eliminar. Não fazer blind retry.

## Resolução (2026-08-07, branch poseidon-v2)

Corrigido em duas camadas, seguindo o desenho-alvo abaixo:

1. **Guard semântico no despacho** (`ChiefBacklogLoopService`, resolução de modelo): o
   `model_id` da rota agora é resolvido junto com o provider do modelo
   (`GetModelAsync` → `GetProviderAsync(model.ProviderId).Kind`) e comparado com o
   `ProviderKind` da conta selecionada. Divergência descarta o modelo (a tentativa roda no
   default da conta), com log dedicado (`LogModelDroppedCrossProvider`) e divergência
   auditável no ledger (`requested_model ≠ resolved_model`) — espelho exato do tratamento do
   effort da Onda 0.4.
2. **Defesa em profundidade no `ModelRouter.Route`**: `ModelRoutingRequest` ganhou
   `PreferredModelProviderKind` (opcional); modelo com provider declarado divergente do
   provider da conta nunca é ecoado — `SelectedModel = null`,
   `DecisionReason = "model_router.cross_provider_model_dropped"`. Provider desconhecido não
   bloqueia (nulo = desconhecido; o guard semântico vive no chamador).

Provas: `ModelRouterDropsModelWhoseProviderDiffersFromSelectedAccount`,
`ModelRouterKeepsModelWhenProviderKindMatchesIgnoringCase`,
`ModelRouterKeepsModelWhenProviderKindIsUnknown` em
`tests/Harness.UnitTests/Providers/CapacityManagerAndModelRouterTests.cs`.

## Desenho correto (base da resolução acima)

A cadeia de resolução deveria ser:

```
Requested Capability → Role → Provider → Account → Account Environment
  → Supported Models → Resolved Model → Supported Effort → Resolved Effort
```

Nunca "o agente diz opus, a Codex recebe opus". Cada provider resolve a MESMA intenção semântica
(ex.: `capability=strong_coding, effort=high`) para a própria representação de modelo. Este é o
desenho-alvo; a implementação fica para depois da janela de observação, e só depois de medirmos o
impacto real de uma conta indisponível sobre o throughput dos demais executores.
