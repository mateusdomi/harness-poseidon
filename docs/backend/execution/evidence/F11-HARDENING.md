# Evidência F11-1 — hardening de release: upgrade, threat model, SBOM, fixture PG

Data: 2026-07-19.

- **Upgrade de qualquer prefixo histórico (`SqliteMigrationUpgradeTests`)**: bancos parados nas migrations 10, 22 e 33 (instalações antigas simuladas com a contabilidade real de `schema_migrations`) sobem até a cabeça (34) com o runner idempotente (re-aplicação 0) e ficam utilizáveis — perfil criado pós-upgrade nasce admin com as colunas novas (`role`, `external_subject`) preenchidas por default. 3/3 verdes.
- **Threat model STRIDE** (`docs/backend/security/THREAT_MODEL.md`): superfícies API/hub, canais, execução isolada, uploads, persistência e licenciamento com as mitigações implementadas referenciadas a testes; 4 riscos residuais registrados explicitamente (divergência WorkChain PG×SQLite, hub aberto no loopback do modo pessoal, SAST via analisadores .NET, flakiness PG).
- **SBOM** (`tools/backend/sbom.sh` → `docs/backend/security/sbom.json`): 28 projetos, 77 pacotes únicos com versões resolvidas dos lock files (diretos + transitivos); regenerável a cada release.
- **Fixture PG concorrente**: janela de health dobrada (60 tentativas × 1s no container, 120 × 500ms no wait) e as três classes com container serializadas na collection xUnit `managed-postgres` — elimina o arranque simultâneo de containers dentro do assembly.
- **Flake real caçado e corrigido (fake do Telegram)**: `FreePort()` soltava a porta antes do bind do `HttpListener` (TOCTOU) — sob o martelo de ~2000 requisições do teste de rate limit a porta era recapturada no intervalo. Bind agora tenta portas novas até vingar e o `Close()` tolera a revalidação de prefixo do macOS. Assembly de integração rodou **3× consecutivas 63/63 verde**.

Gate: format sem mudanças; build Release zero warnings/erros; suíte integral **226/226** (223 + 3 upgrades).
