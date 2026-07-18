# Evidência F1 — contrato e schema durável de realtime

- Executado em: 2026-07-18T17:35:42Z
- Incremento: F1-WRK-1d.1
- Resultado: verde

`IRealtimeEventStore` define append idempotente por `messageId`, sequência crescente por stream e leitura snapshot+delta. A borda exige ULIDs canônicos, stream namespaced sem whitespace, tipo limitado, payload JSON objeto e timestamp UTC. Payloads são canonicalizáveis para comparação estrutural entre SQLite text e PostgreSQL `jsonb`.

Migrations independentes criam `realtime_streams` e `realtime_events`. O stream mantém `last_sequence`; cada evento possui unicidade global por mensagem e unicidade `(tenant, stream, sequence)`, FK composta para isolamento e índices de delta/latest-by-type. Triggers recusam update/delete do histórico em ambos providers.

Seis testes de contrato validaram payload canônico, streams inválidos, JSON não objeto, UTC e cursor. Migrations executaram `SQLite 8→0` e `PostgreSQL 9→0`; os dois schemas materializaram as tabelas, aceitaram um envelope válido e recusaram mutação do evento append-only. A primeira execução integral revelou apenas expectativas antigas de contagem nos testes de recuperação; ajustadas para as novas migrations, a suíte Recovery voltou a 4/4 sem alteração do comportamento de recuperação.

Gate integral `tools/backend/verify.sh`: exit code 0, restore locked/format verdes, build Release com 0 warnings/0 errors e 102/102 testes (`Unit 71`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 15`). Inventário Docker preservou 11 containers de terceiros parados, 14 volumes e 7 networks; cleanup final confirmou zero recursos com `com.harness.managed=true`.

Próximo incremento: implementar os stores SQLite/PostgreSQL com alocação transacional de sequência, replay estrutural por message ID, concorrência e snapshot+delta equivalentes.
