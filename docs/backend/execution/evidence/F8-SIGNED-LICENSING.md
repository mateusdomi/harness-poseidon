# Evidência F8-1 — licenciamento por documento assinado Ed25519

Data: 2026-07-19.

O licenciamento individual ganhou o modelo definitivo da missão sobre o F2-LIC-1: **documentos de licença assinados com Ed25519** (BouncyCastle 2.6.2 — o .NET 10 não expõe Ed25519 puro, apenas compostos ML-DSA; ADR implícita registrada aqui), com validação **100% offline** — a instalação só precisa da chave pública em `Harness:Licensing:PublicKey`.

- `LicenseDocument` tipado (ULID, produto fechado "Poseidon", licenciado, fingerprint opcional de dispositivo, emissão/expiração, grace 0–90 dias, entitlements não vazios, versão) com payload canônico determinístico (JSON camelCase na ordem de declaração) assinado/verificado por `LicenseSigning`; `GenerateKeyPair` serve o tooling do emissor e os testes.
- **Estados fechados** derivados (`active`/`gracePeriod`/`expired`/`revoked`) — a expiração e a revogação **nunca bloqueiam leitura/exportação de dados** (comprovado no teste com leitura de projeto após expiração e após revogação; backup/export continuam pela API de operações).
- **Revogação offline** por lista assinada (`SignedLicenseRevocationList` com a mesma chave): importação valida a assinatura, é idempotente, audita `license.revocationsImported`, muda o estado da licença corrente para `revoked` e passa a recusar reativação da licença revogada.
- API: `GET /api/v1/licenses/signed`, `POST /activation` (409 sem chave pública configurada; 400 assinatura inválida com auditoria `license.signedActivationRejected`; ativação audita `license.signedActivated`), `POST /revocations`. Persistência na migration 0031 (`signed_licenses` + `license_revocations`). OpenAPI republicado.

Testes: 5 unitários (roundtrip com par gerado; adulteração, chave errada, assinatura/chave malformadas; lista de revogação independente; validator fechado; derivação de estados incluindo grace) e integração completa por HTTP com par Ed25519 real gerado no teste: 404 sem licença → licença **expirada** ativa como `expired` **sem bloquear dados** → documento adulterado 400 → ativação válida `active` com 5 entitlements → lista de revogação com issuedAt alterado 400 → importação válida (1 revogação) → estado `revoked`, reativação recusada e **dados ainda legíveis**.

Gate: format sem mudanças; build Release zero warnings/erros; suíte integral 217/217 (`Unit 121`, `Integration 54`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`); SQLite `31→0`. Pendências F8 conscientes para o GNG-4: tooling de emissor empacotado e teste em máquina limpa junto com o instalador F7.
