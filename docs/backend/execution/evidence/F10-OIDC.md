# Evidência F10-8 — maquinaria OIDC completa (falta apenas o Entra ID real)

Data: 2026-07-19.

## Entregas

- **Configuração fechada `Harness:Auth:Oidc`** (`Enabled`, `Authority`, `Audience`, `RequireHttpsMetadata`): validada no boot (fail-fast sem Authority/Audience); desligada por padrão — zero impacto no modo pessoal e nos fluxos existentes.
- **JWT bearer real** (`Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.10): discovery + JWKS do provedor, `MapInboundClaims=false` (claims crus `sub`/`oid`/`name`), audiência validada, token via query string `access_token` aceito no hub de eventos (SignalR).
- **Migration 0034 dual**: `external_subject` em `local_users` com índice único parcial — vínculo 1:1 subject externo ↔ perfil local nos dois providers; `GetByExternalSubjectAsync` nos dois stores.
- **`OidcProfileProvisioner`**: primeiro subject autenticado faz bootstrap do tenant (admin); os demais aderem como member; corrida de bootstrap resolvida por retry de adesão e corrida do mesmo subject resolvida pelo índice único + re-leitura (`DbException`).
- **`OidcSessionMiddleware`**: exige principal autenticado em `/api/*` e `/hubs/*` (401 `oidc_unauthorized`), resolve o perfil do subject e o publica em `HttpContext.Items`; `LocalProfileSession` passou a resolver Items→cookie, então **todos os endpoints existentes funcionam inalterados sob OIDC** (RBAC/ABAC inclusive). `/health` permanece aberto.
- OIDC habilitado implica modo multiusuário (`HarnessServerOptions.Multiuser`) mesmo em SQLite.

## Teste (`OidcServerModeHostTests`)

IdP fake local (discovery + JWKS + RS256 com chave RSA de teste) e Host real: sem token → 401; Alice → provisionada admin (idempotente na segunda chamada); Bob → member no mesmo tenant; Bob não edita perfil de Alice (403), Alice edita o de Bob (200); Bob cria organização (201); token com audiência errada → 401. Verde na primeira execução (~600ms).

## O que falta para fechar o F10 (única dependência externa)

Smoke contra o **Entra ID real** — mesma configuração, trocando apenas:

| Configuração | Valor do Entra ID |
|---|---|
| `Harness:Auth:Oidc:Authority` | `https://login.microsoftonline.com/<TENANT_ID>/v2.0` |
| `Harness:Auth:Oidc:Audience` | Application ID URI ou Client ID da app registration |

Dados necessários do usuário: **Tenant ID** e **Client ID** (app registration com exposição de API/escopo). Nenhum secret de cliente é necessário para validação de tokens no backend.

## Gate

Format sem mudanças; build Release zero warnings/erros; suíte integral **223/223**; migrations **34→0** idempotentes nos dois providers.
