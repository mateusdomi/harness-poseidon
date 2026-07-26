# Observabilidade

## Pipeline

Host, Runner, engine, executores, canais e endpoints emitem OpenTelemetry. O
Collector aplica redação e encaminha traces ao Tempo, métricas ao Prometheus, logs
ao Loki e invocações e avaliações ao Langfuse. Grafana consulta as fontes para
dashboards, alertas e SLOs.

## Correlação

Spans propagam, quando aplicável, `tenant_id`, `project_id`, `conversation_id`,
`work_task_id`, `execution_id`, `attempt_id`, `agent_id`, `tool_call_id` e
`model_invocation_id`.

Operações prioritárias: criação e aquisição de execução, heartbeat, lease, retry,
watchdog, outbox, turno da Bruna, invocação de agente, tool call, canal e endpoint.
Semântica GenAI usa atributos `gen_ai.*`, duração e uso de tokens.

## Segurança e custo

Payloads são opt-in. PII, prompts, respostas, argumentos e segredos são redigidos
antes da exportação. O banco mantém agregados de tokens, custo e duração para
dashboard por assinatura; traces preservam granularidade e correlação.

SLOs incluem latência p95 por fase, recuperação, disponibilidade por provider,
taxa de erro e custo por resultado aprovado.
