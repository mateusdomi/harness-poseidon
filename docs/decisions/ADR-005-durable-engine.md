# ADR-005 — Motor durável específico atrás de interface

- Status: aceito
- Data: 2026-07-18

## Decisão

`IDurableExecutionEngine` cobre ciclo de vida, eventos, timers, retry, leases, fencing, heartbeats, checkpoints, dead-letter e reconciliação. A implementação é específica ao produto e persistida no banco.

## Consequências

Não se cria um framework genérico concorrente. Temporal pode ser backend enterprise futuro da mesma interface, após medição e ADR.

O contrato F1 explicita estados `Ready`, `Running`, `Paused`, `WaitingForRetry`, `WaitingForSignal`, `Completed`, `Cancelled` e `DeadLetter`, com terminais sem transição de saída. Operações esperadas retornam status tipado em vez de exceção: start/pause/resume/cancel, aquisição/renovação/heartbeat com fencing, checkpoint, conclusão/falha, sinal, timer, reconciliação e consulta. Retry usa política decimal determinística, limitada por `MaximumDelay`; a implementação nunca pede ao LLM para decidir backoff ou validade de transição.

O schema F1 materializa estado atual separado de attempts, checkpoints, timers, signals, histórico de transições, dead-letter, Inbox de comandos e Outbox de transições. Índices provider-specific selecionam execuções prontas, leases expiradas, timers vencidos e Outbox pendente. `active_attempt_id` permanece uma invariante transacional da aplicação, sem FK circular, enquanto cada attempt possui FK para execution/tenant e unicidade de número e fencing token.

Provider adapters obrigatoriamente reutilizam `DurableExecutionStateCodec`, `DurableAttemptStateCodec` e `DurableExecutionContractValidator`; não mantêm mapas ou validações divergentes. A Inbox compara `DurableCommandHash` SHA-256 da representação tipada. Lease é limitado a 24 horas e fencing token deve ser positivo em toda escrita pertencente a attempt.

No modo pessoal, `SqliteDurableExecutionEngine` envia cada operação ao single-writer dispatcher. Start/lifecycle/attempt/checkpoint/signal/timer/reconciliação usam transaction explícita; mudanças de estado anexam histórico e Outbox antes do commit. Heartbeat e renewal alteram a versão sem fabricar uma transição de estado. Completion, failure, pause, timer-wait e reconciliação limpam o attempt ativo e incrementam fencing para rejeitar qualquer escrita atrasada.

No modo servidor, `PostgresDurableExecutionEngine` mantém a mesma interface e os mesmos testes de comportamento. A aquisição seleciona trabalho elegível com `FOR UPDATE SKIP LOCKED`; demais mutações serializam por execução com advisory lock transacional e bloqueiam as linhas do agregado durante a leitura. A Inbox, o estado atual, attempts, checkpoints, histórico e Outbox são confirmados na mesma transação. JSON é comparado semanticamente nos testes comuns porque `jsonb` normaliza sua representação textual sem alterar o valor.
