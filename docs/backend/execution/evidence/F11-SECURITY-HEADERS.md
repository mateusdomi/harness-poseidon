# Evidência F11-2 — hardening de borda: security headers, cookie e CORS default-deny

Data: 2026-07-19.

`SecurityHeadersMiddleware` (`src/Harness.Host/Security/SecurityHeadersMiddleware.cs`) é o
**primeiro middleware do pipeline** e aplica, de forma síncrona antes de `next`, os seguintes
cabeçalhos a **toda** resposta (SPA estática, API e hub):

- `Content-Security-Policy`: `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline';
  img-src 'self' data: https:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none';
  base-uri 'self'; form-action 'self'; object-src 'none'` — compatível com o bundle servido
  (verificado: `wwwroot/index.html` só carrega scripts/estilos externos por `src`/`href`, sem
  inline script).
- `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`.
- `Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=(), usb=()`.
- `Cross-Origin-Opener-Policy: same-origin`, `Cross-Origin-Resource-Policy: same-origin`.
- `Strict-Transport-Security: max-age=31536000; includeSubDomains` **apenas** quando a requisição
  é HTTPS (o modo pessoal em loopback HTTP não o recebe).

Configurável por `Harness:Security:Headers` (`SecurityHeadersOptions`, tipado): `Enabled` (default
`true`) e `ContentSecurityPolicy` (override opcional). Não sobrescreve cabeçalho já definido.

**Cookie de sessão** (`SetSessionCookie`): `HttpOnly` + `SameSite=Strict` mantidos; `Secure` agora
derivado de `Request.IsHttps` — seguro sob TLS (modo servidor) e ainda entregue em loopback HTTP
(modo pessoal).

**CORS default-deny**: nenhuma política CORS permissiva está habilitada; origem externa não recebe
`Access-Control-Allow-Origin`.

## Testes (`tests/Harness.IntegrationTests/Security/SecurityHeadersTests.cs`)

- End-to-end no Host real (SQLite, loopback HTTP): `/health` e `/` (SPA) carregam a CSP e os
  cabeçalhos; HSTS ausente sobre HTTP; `Origin: https://evil.example` **não** recebe ACAO; o
  `Set-Cookie` da criação de perfil tem `HttpOnly`+`SameSite=Strict` e **sem** `Secure` sob HTTP.
- `MiddlewareEmitsHstsOnlyOverHttps` (Theory http/https): HSTS presente só sobre HTTPS.
- `DisabledMiddlewareEmitsNoHeaders`: com `Enabled=false` nenhum cabeçalho é emitido.

Gate: format sem mudanças; build Release 0 warnings/0 erros; foco Security/Identity/Frontend/OIDC
7/7 verde; suíte integral **230/230** (226 + 4 novos). Frontend preservado (nenhuma alteração em
`frontend/**` ou `docs/frontend/**`); SPA servida sem regressão sob a nova CSP.
