# Threat model — Harness Poseidon backend (release 1.0)

Data: 2026-07-19. Escopo: Host (.NET 10), Launcher desktop, execução isolada de agentes, canais externos, persistência SQLite/PostgreSQL, licenciamento e modo servidor multiusuário/OIDC.

## Superfícies e ativos

| Superfície | Ativos em risco |
|---|---|
| API HTTP (`/api/v1/*`) + hub SignalR (`/hubs/events`) | dados do tenant, sessões, comandos de execução |
| Canais externos (gateway + Telegram + Teams) | identidade de vínculo, conteúdo de conversas, tokens dos providers |
| Execução isolada (worktree + Docker + CLI de agente) | código-fonte do repositório alvo, credenciais do ambiente, host |
| Uploads (anexos de solicitação, referências visuais) | sistema de arquivos, integridade do pipeline |
| Persistência (SQLite local / PostgreSQL gerenciado) | todos os dados, trilha de auditoria |
| Licenciamento assinado | modelo comercial, integridade offline |

## STRIDE por superfície (mitigações implementadas)

### API + hub
- **Spoofing**: modo pessoal usa cookie HttpOnly/SameSite=Strict de perfil local; modo OIDC exige bearer JWT validado por discovery/JWKS com audiência fechada (`OidcSessionMiddleware`, 401 em `/api` e `/hubs`); cookie forjado com ULID inexistente não abre sessão (testado adversarialmente).
- **Tampering**: OCC (`version`) em todos os agregados; CHECKs de estados fechados no banco como defesa em profundidade; verificação de consistência em três camadas para workflows.
- **Repudiation**: `audit_ledger` append-only com hash encadeado (`AuditLedgerHash`), integridade verificada em teste.
- **Information disclosure**: RBAC (admin/member) + ABAC (posse do perfil); role e external_subject não são expostos no contrato HTTP; erros de execução sanitizados antes de persistir.
- **DoS**: rate limit por IP no modo servidor (FixedWindow, 429), limites de payload dos uploads, `QueueLimit=0`.
- **Browser hardening**: `SecurityHeadersMiddleware` (primeiro do pipeline) aplica a toda resposta CSP restritiva (`default-src 'self'`, `script-src 'self'`, `frame-ancestors 'none'`, `object-src 'none'`), `X-Content-Type-Options=nosniff`, `X-Frame-Options=DENY`, `Referrer-Policy=no-referrer`, `Permissions-Policy` mínima, COOP/CORP `same-origin` e HSTS **apenas** sobre TLS; cookie de sessão `HttpOnly`+`SameSite=Strict`+`Secure` quando HTTPS. **CORS default-deny**: nenhuma política permissiva habilitada — origem externa não recebe `Access-Control-Allow-Origin` (testado).
- **Elevation of privilege**: `AutonomousActionGuard` nega as 7 ações invioláveis a atores automatizados em todos os modos; aprovação forjada negada; gates de workflow não-contornáveis (409/400 testados).

### Canais externos
- **Spoofing**: vínculo explícito por identidade externa antes de aceitar turnos; turn id determinístico por id externo (dedupe garante zero duplicação em reentrega). Teams exige bearer de gateway comparado em tempo constante.
- **Information disclosure**: tokens Telegram/Teams só por configuração externa — nunca em repo, banco, docs ou logs. Anexos Teams entram somente como metadados; nenhuma URL remota é buscada.
- **DoS/SSRF**: Telegram usa long polling com backoff e dedupe por `update_id`; Teams limita payload/anexos, retenta 429/5xx e aceita `serviceUrl` HTTPS em allowlist exata (HTTP apenas em loopback de teste).

### Execução isolada
- **Elevation/escape**: sandbox Docker com `no-new-privileges`, limites de memória/CPU/pids, rede dedicada, worktree por tentativa; cleanup preserva worktree suja (sem perda de trabalho) e claims usam lease+fencing token (owner antigo recusado — provado em recovery).
- **Tampering**: inbox idempotente por SHA-256; branch ativa única por repositório e worktree única por tenant (constraints do banco).

### Uploads
- **Tampering/malware**: `AttachmentIngestPolicy` — allowlist de extensões, magic bytes de executáveis rejeitados, anti zip-bomb (razão de expansão), path traversal bloqueado, quarentena confinada, hash SHA-256 e auditoria de aceite/rejeição.

### Persistência
- **Tampering**: migrations embutidas idempotentes (34→0) com upgrade de qualquer prefixo histórico testado; backup/restore locais respondem 409 no modo servidor (PostgreSQL gerenciado é a autoridade); `signed_licenses.document_json` em `json` puro para preservar a assinatura.
- **Information disclosure**: senha do PG de teste via secret file com permissão 600; connection string só por configuração.

### Licenciamento
- **Spoofing/tampering**: Ed25519 sobre payload canônico; validação offline pela chave pública; revogação por lista assinada idempotente; dados permanecem legíveis pós-expiração (sem reféns).

## Riscos aceitos / residuais (registrados)

1. **Semântica divergente PG×SQLite em WorkChain** (`gate.changed` vs `task.stateChanged`, payload do Finalize) — registrada na onda 5; unificação planejada.
2. **Hub de eventos no modo pessoal é aberto no loopback** — aceitável para desktop single-user; no modo OIDC o hub exige bearer.
3. **SAST/dependências**: analisadores .NET (CA*/IDE*) com `TreatWarningsAsErrors`, `NuGetAudit=all` e Semgrep dedicado via `verify-sast.sh`. O rule comunitário C# de SQL é excluído porque marcou 85 usos de SQL constante/parametrizado na DAL; uma regra local proíbe SQL interpolado fora de persistência/migração. Essa exceção deve ser reavaliada se a fronteira de SQL mudar.
4. **Flakiness de containers PG concorrentes** — mitigada com janela de health dobrada (60s); reavaliar se recorrer.

## SBOM

`tools/backend/sbom.sh` gera `docs/backend/security/sbom.json` (pacotes diretos e transitivos por projeto, com versões resolvidas dos lock files). Regenerar a cada release.
