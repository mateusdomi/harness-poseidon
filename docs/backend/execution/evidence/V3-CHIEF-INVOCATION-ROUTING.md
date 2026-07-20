# V3 — routing e override por invocação do Chief

`POST /api/v1/conversations/{id}/turns` aceita overrides opcionais e tipados de conta, modelo,
effort, fallbacks e motivo. Sem override, o Host decide deterministicamente a partir da seleção da
instância do Chief e dos defaults versionados da definição. A decisão exige conta ativa do mesmo
provider, capability `chat`, modelo/fallbacks habilitados e mapping explícito de
`low|medium|high|max` para o valor do provider.

A seleção efetiva é persistida no mailbox durável pela migration dual
`0044_chief_invocation_selection`: conta, ID/nome do modelo, esforço canônico e traduzido,
fallbacks, origem (`explicit|agent|definition`), motivo, custo estimado e cota restante. A
estimativa usa tokens de entrada conservadores (`ceil(caracteres/4)`) mais 1.000 tokens de saída;
modelos locais sem preço preservam custo nulo em vez de inventar telemetria. Custo superior à
cota disponível é recusado antes do enqueue.

A decisão integra o hash idempotente do comando, sobrevive a restart, gera ledger
`chief.invocationRouted` + `audit.eventAppended` e acompanha o lease até o executor. O adapter
Codex app-server envia `model` e o effort provider-specific em `turn/start`, conforme o schema
gerado pela CLI 0.144.6.

Evidência automatizada:

- HTTP real persiste override completo, custo/cota e fallbacks;
- behavior provider-neutral reidrata a decisão em SQLite e PostgreSQL;
- protocolo fake recusa implicitamente qualquer `turn/start` que omita modelo/esforço esperados;
- migrations frescas, upgrades históricos e recovery avançam até 44.
- gate integral: 248/248 testes backend, 410/410 frontend, build Release sem warnings,
  SAST com zero achados e varredura de segredos limpa.
