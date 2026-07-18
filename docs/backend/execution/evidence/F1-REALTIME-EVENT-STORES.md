# Evidência F1 — stores sequenciados de realtime

- Executado em: 2026-07-18T17:41:03Z
- Incremento: F1-WRK-1d.2
- Resultado: verde

`SqliteRealtimeEventStore` executa todo append pelo dispatcher único. `PostgresRealtimeEventStore` combina advisory lock por message ID com row lock no stream head. Ambos alocam a sequência e inserem o evento na mesma transação, canonicalizam JSON para replay equivalente entre text/`jsonb`, recusam reutilização conflitante do message ID e recompõem `latestByType` + delta ordenado.

O comportamento comum partiu de stream vazio e disparou dez appends concorrentes. Os receipts resultaram exatamente nas sequências 1–10, únicas e contíguas. Reenvio do primeiro message ID retornou seu receipt com `replay=true` sem avançar o head; payload conflitante foi recusado e também não criou lacuna. Um 11º evento atualizou o latest do tipo correspondente. Snapshot após sequência 7 retornou delta `[8,9,10,11]`, head 11 e latest correto para dois tipos; snapshot integral continha 11 message IDs distintos.

Execuções focadas SQLite 1/1 e PostgreSQL 1/1 passaram com o mesmo cenário. Gate integral `tools/backend/verify.sh`: exit code 0, restore locked/format verdes, build Release com 0 warnings/0 errors e 102/102 testes (`Unit 71`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 15`). Inventário Docker preservou 11 containers de terceiros parados, 14 volumes e 7 networks; cleanup final confirmou zero recursos com `com.harness.managed=true`.

O primeiro gate integral expôs uma corrida na criação simultânea do head PostgreSQL: `ON CONFLICT (stream_name)` não cobria a PK composta concorrente. A correção usa `ON CONFLICT DO NOTHING`, seguida obrigatoriamente de row lock e validação do tenant. Uma repetição também eliminou do teste a suposição inválida de que ordem de comando concorrente define a sequência; o latest agora é comparado ao maior sequence efetivamente recebido para o tipo. Cenário focado e gate integral passaram depois das duas correções instrumentadas.

Próximo incremento: implementar resolução de stream, sink Outbox→store→SignalR, trocar o snapshot HTTP para o store persistido e provar replay/resync após restart do Host.
