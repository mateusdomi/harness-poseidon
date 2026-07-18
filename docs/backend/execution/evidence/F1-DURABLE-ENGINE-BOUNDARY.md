# Evidência F1 — borda comum do motor durável

- Executado em: 2026-07-18T14:13:19Z
- Incremento: EP-05c (borda comum)
- Resultado: verde

Antes dos adapters SQL, foram congeladas as regras comuns que ambos devem reutilizar: codecs bidirecionais de todos os estados de execution/attempt; validação de IDs ULID canônicos, JSON, campos limitados, idempotency key, owner e fencing; lease positivo limitado a 24 horas; e hash SHA-256 determinístico do comando tipado para a Inbox.

Os testes percorreram todos os enums nos codecs, aceitaram um Start canônico e rejeitaram JSON/ULID inválidos, fencing zero e lease de dois dias. Gate: `tools/backend/verify.sh` exit 0; build Release 0 warnings/0 errors; 61/61 testes verdes (`Unit 37`, `Architecture 6`, `Concurrency 3`, `Recovery 2`, `Contract 3`, `Integration 10`). Esta evidência não declara os adapters SQL concluídos.
