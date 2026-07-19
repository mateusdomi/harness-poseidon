# Evidência F2-LIC-1 — licenças e entitlements

Data: 2026-07-18. Commit funcional publicado: `c831161` em `develop`.

O modo pessoal mantém uma licença persistida por tenant/dispositivo. Antes da ativação ela é `unlicensed`; `POST /api/v1/licenses/activation` normaliza e valida `XXXX-XXXX-XXXX-XXXX`, guarda somente SHA-256 da chave e registra auditoria global com apenas o primeiro bloco visível. A ativação concede validade anual, grace de 14 dias e cinco entitlements Pro, incluindo limites numéricos e OIDC explicitamente não incluído.

O estado exposto é derivado pelo relógio em `active`, `offline`, `gracePeriod`, `expired` ou `unlicensed`, sem mutação oportunista. O cenário HTTP comprovou sessão obrigatória, formato inválido, ativação, chave integral ausente da auditoria, contratos completos, cinco entitlements e persistência. Depois de simular validade e grace encerrados, o Host reiniciado devolveu `expired`, mas continuou permitindo leitura do projeto e dos entitlements — a expiração nunca sequestra os dados.

OpenAPI/drift coincidem com `License`, `Entitlement` e o comando de ativação do frontend. `tools/backend/verify.sh` passou com restore locked, format, build Release 0 warnings/0 errors e 166/166 testes (`Unit 93`, `Integration 33`, `Contract 27`, `Recovery 4`, `Architecture 6`, `Concurrency 3`). SQLite ficou `26→0`, PostgreSQL `11→0`; `frontend/**` e `docs/frontend/**` permaneceram sem edição backend.
