# F2-ORG-1 — Organizações pessoais

Data UTC: 2026-07-18

## Fatia vertical executada

- O módulo Organizations agora contém agregado, marca herdável, policies/templates projetados e validação de nome, slug, plano e limites.
- Requests de create/update recusam campos desconhecidos; PATCH diferencia propriedade ausente e `null`, preserva campos omitidos e usa versão interna para OCC.
- Migration SQLite `0010_organizations` amplia a tabela fundacional com slug, plano, marca e coleções JSON; slug é único por tenant sem distinção de caixa.
- `SqliteOrganizationStore` usa exclusivamente o dispatcher compartilhado, filtra toda leitura/mutação por tenant e retorna conflito determinístico para nome/slug duplicado.
- API pessoal publica `GET/POST /api/v1/organizations` e `GET/PATCH /api/v1/organizations/{organizationId}`, paginação por cursor e RFC 7807.
- A sessão `harness.profile` resolve o tenant da organização; requisição sem perfil recebe 401 e IDs de outro tenant não podem ser observados.
- A resposta possui exatamente os nove campos de `organizationSchema`; versão OCC permanece interna.
- O OpenAPI canônico foi republicado e um drift test lê o schema TypeScript sem alterar `frontend/**`.

## Evidência executada

O teste integrado recusou acesso anônimo, criou perfil e organização com marca, comprovou unicidade case-insensitive do slug, aplicou PATCH parcial, paginou, encerrou o Host e recuperou organização/marca depois do restart no mesmo banco. A migration foi executada do zero e reaplicada como no-op.

```text
tools/backend/verify.sh
exit code: 0
Release build: 0 warnings, 0 errors
Unit: 75/75
Integration: 19/19
Contract: 5/5
Recovery: 4/4
Architecture: 6/6
Concurrency: 3/3
Total: 112/112
```

Migrations: SQLite `10→0`; PostgreSQL permanece `9→0`. Nenhum processo ou recurso Docker Harness ficou órfão.
