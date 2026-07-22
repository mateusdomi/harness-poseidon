# Cota, agendamento de retorno e retry inteligente (loop autônomo)

Data: 2026-07-22. Branch: `develop`. Base: `c1a4d59`.

## Objetivo

Quando o chefe delega trabalho aos agentes, a cota deles (menor que a do chefe) acaba. O
sistema precisa: identificar que a cota esgotou, salvar quando a conta volta, recusar
trabalho até lá, reabilitar sozinho no reset e repetir falhas transitórias com backoff — sem
ação humana. É o coração do loop autônomo (peço→delego→verifico cota/conclusão→sigo).

## Peças entregues

### 1. Classificação do desfecho — `AgentRunOutcomeClassifier` (puro)

Mapeia `status` + `failureCode` OBSERVADOS numa categoria fechada:
`Completed` / `Cancelled` / `QuotaExhausted` (aguarda reset, cooldown conservador padrão 3h) /
`AuthenticationRequired` (escala, exige login) / `Transient` (retry com backoff — onde o
GLM/Z.AI instável cai) / `Permanent` (escala, nunca retry cego). Padrões reais: cota/rate-limit,
not-logged-in, connection-reset/timeout/no-output/exit-code. 16 testes unit.

### 2. Ledger DURÁVEL de disponibilidade — `AccountAvailabilityLedger`

Persiste por conta (em `~/.harness/account-availability.json`, fora do repo, sem segredo):
estado fechado, **quando a conta volta** (`CooldownUntil`), motivo tipado e falhas seguidas
(para o backoff). Sobrevive a restart do Host. `RecoverExpired(now)` reabilita as contas cujo
cooldown venceu — e **contas `AuthenticationRequired` nunca voltam sozinhas** (exigem login).
Guarda a data/hora EXATA de volta (ex.: reset semanal do Kimi). 5 testes unit.

### 3. Integração no orquestrador (`AgentRunOrchestrator`)

- **Registra o desfecho** após cada execução: cota → grava `QuotaLimited` com a data/hora de
  volta; login → `AuthenticationRequired`; transitório → `CoolingDown` com **backoff
  exponencial capado** (30s·2^falhas, teto 15min); sucesso → zera o histórico.
- **Recusa trabalho numa conta indisponível** antes de adquiri-la (2b): uma conta em
  cota/cooldown/login com janela ativa é rejeitada com o motivo tipado — não se queima
  tentativa numa conta que já sabemos fora.

### 4. Agendador de retomada — `AccountRecoveryBackgroundService`

A cada 1 min reabilita, no ledger, as contas cujo cooldown resetou — resiliente (uma falha de
ciclo não derruba o laço). Só reabilita a DISPONIBILIDADE; o re-despacho do trabalho pendente
é decisão do nível superior (Chief), próximo passo.

## O que fecha e o que falta

Fecha: detectar cota/falha → salvar quando volta → recusar até lá → reabilitar no reset →
backoff para transitório. **Falta** (próximas fatias): o **re-despacho** automático do
trabalho pendente quando a conta volta (loop do Chief) e a **notificação** de bloqueio
(escolha de canal do operador — Telegram bloqueado na rede da empresa; email pendente).

Regressão verde: Unit 324/324, Contract 41/41, Architecture 7/7, format limpo, build Release
0 warnings, secrets ok.
