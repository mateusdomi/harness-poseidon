# Contrato de eventos

## Envelope

Eventos duráveis e realtime compartilham `stream`, `sequence`, `type`,
`occurredAt` e `payload`. Identificadores de correlação aplicáveis incluem
`tenant_id`, `project_id`, `conversation_id`, `work_task_id`, `execution_id`,
`attempt_id`, `agent_id`, `tool_call_id` e `model_invocation_id`.

`sequence` é monotônica dentro do stream. Consumidores deduplicam por stream e
sequência e recuperam lacunas pelo endpoint de snapshot.

## Transição de card

Uma transição registra `card_id`, estado anterior, estado seguinte, evento, ator,
timestamp, motivo e referência de evidência. O registro no `audit_ledger` é
append-only e precede a atualização das projeções observáveis.

## Entrega

Eventos externos usam outbox transacional. Retry não duplica efeitos; consumidores
devem ser idempotentes. Payload desconhecido não é descartado silenciosamente e
versão incompatível não é interpretada por aproximação.

O contrato executável de realtime está em `docs/contracts/events.json`; ele é
validado pelos testes de drift do frontend.
