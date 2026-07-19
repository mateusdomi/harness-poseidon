# V3 — severidade tipada dos eventos de tentativa

Data: 2026-07-19

## Resultado

`AttemptEvent` agora possui `severity` persistida com vocabulário fechado
`info|warning|error|critical`. Eventos de início e submissão nascem `info`; a nota da revisão
rejeitada nasce `error`, enquanto aprovação permanece `info`. O cliente não precisa inferir
criticidade a partir de texto ou `kind`.

## Evidência

- migration dual `0042_attempt_event_severity`, com default compatível para histórico e índice;
- mutações SQLite/PostgreSQL gravam a severidade na mesma transação da tentativa/revisão;
- API comprova a sequência `info, info, error` no ciclo com rejeição;
- OpenAPI e drift de contrato atualizados sem alterar `frontend/**` ou `docs/frontend/**`;
- upgrade histórico e contadores de migrations elevados para 42.
