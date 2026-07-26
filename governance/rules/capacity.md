# Capacidade e cotas

## Semântica honesta

Capacidade é decidida a partir de snapshots com provedor, conta, assinatura,
estado, confiança, origem, instante observado, validade e reset conhecido. O estado
é `Unknown`, `Available`, `NearLimit` ou `Exhausted`.

`remaining_fraction` fica nula quando o provedor não oferece valor confiável.
Desconhecido nunca é convertido em percentual inventado. Snapshot expirado perde
força de decisão e aciona coleta ou fallback.

## Agendamento

- O scheduler considera adequação do agente, modelo, provedor, conta, restrições,
  risco, custo, concorrência e contexto.
- Fallback, circuit breaker e backpressure seguem regras determinísticas.
- Redistribuição preserva card, idempotency key, context snapshot e checkpoints.
- Conta exaurida não recebe nova tentativa até reset comprovado ou override humano.
- Override registra ator, motivo, prazo e evidência.
- Decisões do router são auditáveis e correlacionadas à execução.

Capacidade não autoriza ampliar escopo nem relaxar qualidade. Quando nenhuma rota
segura existe, o card permanece bloqueado com causa explícita.
