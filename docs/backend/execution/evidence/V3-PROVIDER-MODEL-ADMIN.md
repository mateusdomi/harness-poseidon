# V3 — administração tipada de modelos de provider

O catálogo de modelos deixou de ser somente leitura/sync com edição superficial. A API agora
oferece `POST /api/v1/models`, `PATCH /api/v1/models/{id}` e `DELETE /api/v1/models/{id}`.
Modelos criados manualmente nascem desabilitados; nome no provider, nome de exibição,
capabilities, janela de contexto, custos de entrada/saída e mappings de esforço são validados por
tipos e vocabulários fechados. Esforço aceita somente `low`, `medium`, `high` e `max`, sempre com
valor provider-specific explícito.

A remoção é segura: exige modelo desabilitado e recusa referências em definições de agentes,
instâncias runtime e regras de roteamento, incluindo fallbacks persistidos em JSON. Criação,
edição e remoção registram ledger e `audit.eventAppended`; nenhum segredo integra o contrato.

Evidência automatizada:

- comportamento provider-neutral cobre criação, normalização, PATCH integral, lifecycle e remoção
  em SQLite e PostgreSQL;
- cenário HTTP cobre payload inválido, `201`, edição integral, bloqueio `409` e `204`;
- OpenAPI expõe POST/PATCH/DELETE e o teste de drift valida as operações;
- gate integral: 248/248 testes backend, 410/410 frontend, build Release sem warnings,
  SAST com zero achados e varredura de segredos limpa.
