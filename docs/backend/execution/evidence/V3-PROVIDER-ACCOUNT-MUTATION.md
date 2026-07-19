# Evidência v3 — administração segura de conta de provider

Data: 2026-07-19. Resultado: verde.

O catálogo de providers passou a expor `PATCH /api/v1/accounts/{id}` com request C#
fortemente tipado. A mutação permite somente `label`, `state` e `quotaLimitUsd`; o estado é
fechado em `active|disabled|quotaExceeded`, a cota não aceita valor negativo e campo omitido/nulo
preserva o valor atual. Uso acumulado e referência de credencial não são campos graváveis nem aparecem no
contrato HTTP.

O ciclo administrativo agora também inclui `POST /api/v1/accounts` e
`DELETE /api/v1/accounts/{id}`. A criação exige provider existente, identificador ULID e uma
referência de credencial com esquema fechado (`keychain://`, `dpapi://` ou `secret://`), nasce
sempre em estado `disabled` e nunca devolve a referência no response. URLs e segredo em claro são
recusados. A exclusão é fail-closed: somente conta desabilitada e sem budget associado pode ser
removida; conta ativa ou referenciada retorna 409.

SQLite e PostgreSQL aplicam a mudança na mesma transação que o ledger encadeado, o evento de
auditoria e `quota.updated`. O comportamento provider-neutral alterou conta nos dois bancos. O
cenário HTTP comprovou criação sem vazamento da referência, edição, rejeição de esquema/estado
desconhecido, bloqueio da exclusão ativa, exclusão segura, publicação realtime e recuperação de
rótulo/cota após restart do Host. O OpenAPI canônico declara POST/PATCH/DELETE sem mudar os seis
campos públicos de `AccountContract`.

Gates executados:

- teste focado HTTP: 1/1;
- drift do catálogo: 5/5;
- comportamento provider-neutral em SQLite e PostgreSQL gerenciado: 2/2;
- `tools/backend/verify.sh`: exit 0, frontend 362/362, build Release 0 avisos/0 erros e backend
  248/248 (`Unit 123`, `Integration 82`, `Contract 28`, `Recovery 5`, `Architecture 7`,
  `Concurrency 3`);
- `tools/backend/verify-sast.sh`: exit 0, 30 regras sobre 316 arquivos C#, 0 achados.

`frontend/**` e `docs/frontend/**` permaneceram preservados.
