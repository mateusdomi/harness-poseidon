# F2-ID-1 — Perfil local

Data UTC: 2026-07-18

## Fatia vertical executada

- Domínio `LocalProfile` valida e normaliza nome, email, avatar, locale, timestamps UTC e versão.
- Aplicação cria perfil e aplica PATCH distinguindo propriedade ausente de `null` explícito; campos desconhecidos são recusados.
- Migration SQLite `0009_local_profiles` acrescenta email, avatar, locale, last-active e unicidade case-insensitive de email por tenant.
- `SqliteLocalProfileStore` usa o dispatcher único e OCC por versão.
- API pessoal implementa `GET/POST /api/v1/profiles`, `GET /api/v1/profiles/current` e `PATCH /api/v1/profiles/{id}`.
- A criação materializa o tenant pessoal e emite cookie `harness.profile` HttpOnly/SameSite Strict; update só é permitido pela sessão local correspondente.
- Listagem usa envelope `{ items, nextCursor }`; erros usam `application/problem+json`.
- OpenAPI canônico foi republicado e o drift test compara rotas/campos com `frontend/src/api/contracts/core.ts` sem editar o frontend.

## Evidência executada

O teste integrado criou o perfil, comprovou cookie de sessão, leu `/current`, limpou email com `null`, paginou, encerrou o Host, reiniciou no mesmo banco, recuperou a sessão/perfil atualizado e recusou uma segunda criação com 409. Artefatos temporários foram removidos.

```text
tools/backend/verify.sh
exit code: 0
Release build: 0 warnings, 0 errors
Unit: 73/73
Integration: 18/18
Contract: 4/4
Recovery: 4/4
Architecture: 6/6
Concurrency: 3/3
Total: 108/108
```

Migrations: SQLite `9→0`; PostgreSQL permanece `9→0`. Nenhum recurso Docker Harness ou processo ficou órfão.
