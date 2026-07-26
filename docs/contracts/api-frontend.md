# Contrato backend e frontend

## Fontes executáveis

`docs/contracts/openapi.json` define a superfície HTTP publicada e
`docs/contracts/events.json` define o envelope e os payloads realtime. O frontend
consome esses contratos tipados e possui testes de drift contra ambos.

## Compatibilidade

- Mudança incompatível exige versionamento explícito.
- Campos novos são aditivos e consumidores toleram campos desconhecidos.
- Campos obrigatórios, enums, status HTTP e semântica não mudam silenciosamente.
- Erros usam payload tipado e não dependem de texto para lógica do cliente.
- Paginação, ordenação, deduplicação e correlation id são consistentes.

## Gate

Uma mudança de endpoint ou evento inclui atualização do contrato executável,
implementação, cliente e testes no mesmo card. Build, typecheck, lint, testes e
drift devem ficar verdes. Mock e modo HTTP real devem respeitar a mesma semântica.
