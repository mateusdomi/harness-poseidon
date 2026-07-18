# Evidência F1 — sink persistido Outbox→realtime

- Executado em: 2026-07-18T17:47:25Z
- Incremento: F1-WRK-1d.3a
- Resultado: verde

`PersistedRealtimeOutboxSink` resolve o stream, persiste primeiro via `IRealtimeEventStore` e só então publica o envelope pelo broadcaster tipado. Payload com `projectId` canônico usa `project:{id}`; eventos legados sem essa informação usam `tenant:{tenantId}`. Tipos fora do catálogo são recusados. `project.created` foi incorporado ao catálogo tipado e ao artefato `events.json`, pois já é produzido atomicamente pela fundação.

O broadcaster SignalR envia ao grupo do stream no método `event`. Em replay do mesmo outbox message ID, o store devolve o envelope persistido e o sink não transmite novamente. Isso evita nova sequência/broadcast; se houver queda depois do append e antes do envio, o snapshot/delta persistido é a recuperação autoritativa.

Teste com SQLite real abriu o primeiro dispatcher, aplicou migrations, provisionou o projeto e despachou a mensagem: uma transmissão, sequência 1. Após fechar e reabrir o mesmo banco, o segundo sink reaplicou migrations `0`, reprocessou a mesma mensagem e comprovou uma única row/sequence 1 e zero transmissões adicionais. Segundo cenário comprovou o fallback tenant para payload legado. Execução focada: 2/2 verde; gate integral: 104/104, build Release 0 warnings/0 errors.

Próximo incremento: registrar store/sink/worker no Host pessoal, trocar o endpoint snapshot para leitura assíncrona persistida e executar resync end-to-end após restart HTTP/SignalR.
