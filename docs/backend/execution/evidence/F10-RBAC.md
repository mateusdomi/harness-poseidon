# Evidência F10-7 — RBAC/ABAC multiusuário (admin/member)

Data: 2026-07-19.

## Modelo

- **Migration 0033 dual** (SQLite + PostgreSQL): coluna `role` fechada por CHECK (`admin`/`member`) em `local_users`, default `admin` — instalações existentes do modo pessoal (perfil único) tornam-se admin sem passo manual.
- **Papel derivado do regime de criação**: bootstrap do tenant → `admin`; adesão a tenant compartilhado (`JoinExistingTenant`) → `member`. Enum fechada `LocalProfileRole` + codec (`LocalProfileRoleCodec`) no padrão dos demais estados fechados; `LocalProfileRecord.Role` exposto pelos dois providers.
- **Role fora do contrato HTTP**: `ProfileResponse` permanece idêntico ao schema do frontend (Kimi, final) — o drift test de perfil trava os campos; o papel é atributo interno de autorização.

## Enforcement

- **RBAC — `DELETE /api/v1/projects/{id}`**: somente admin (403 `admin_required` para member, inclusive no próprio projeto); 403 adicionado ao OpenAPI canônico republicado (diff aditivo único).
- **ABAC + RBAC — `PATCH /api/v1/profiles/{id}`**: o dono sempre edita o próprio perfil (atributo de posse via cookie de sessão); admin pode editar qualquer perfil do tenant; member em perfil alheio permanece 403.
- No modo pessoal nada muda de comportamento: o único perfil é admin.

## Testes

- **`IdentityCoreStoreBehavior` (dual-provider)**: bootstrap nasce `Admin`; adesão a tenant existente aplica e nasce `Member`; adesão a tenant inexistente é `NotFound` — verificado em SQLite fresco e na bateria do container PostgreSQL.
- **`PostgresMultiuserLoadTests` (Host real em modo servidor)**: após os 30 usuários concorrentes, o banco tem **exatamente 1 admin e 29 members** (o vencedor do bootstrap); member recebe 403 ao deletar projeto (até o próprio), admin deleta o mesmo projeto (204) e edita o perfil de um member (200).
- Contagens de migrations atualizadas para **33→0** idempotente nos dois providers.

## Gate

Format sem mudanças; build Release zero warnings/erros; suíte integral **222/222**. Restante do F10: **somente OIDC/Entra ID** — única dependência externa; os dados da app registration serão solicitados ao usuário como último passo antes do F11.
