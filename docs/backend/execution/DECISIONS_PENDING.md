# Decisões pendentes e suposições

Decisões não bloqueadoras são tomadas pela opção mais segura e promovidas a ADR quando arquiteturais.

| ID | Tema | Estado/decisão provisória | Próxima ação |
|---|---|---|---|
| DP-001 | arquivo físico do prompt v1.3 ausente | usar integralmente o conteúdo fornecido na conversa como fonte de verdade | catalogar sem criar dependência externa |
| DP-002 | tooling local ignorado | resolvido: `.artifacts/` foi adicionado ao `.gitignore` para builds frontend isolados e artefatos efêmeros | manter toda escrita fora de `frontend/**` e `docs/frontend/**` |
| DP-003 | contrato frontend usa 25 eventos e nomes diferentes do catálogo v1.3 | contrato v1.3 prevalece no backend; manter aliases/compatibilidade somente se justificados | detalhar em `docs/contracts/CONTRACT_RECONCILIATION.md` e testes de drift |
| DP-004 | ArchUnitNET versus verificador próprio | resolvido por ADR-014: ArchUnitNET 0.13.3 + verificação estrutural XML | expandir regras conforme tipos de módulo entrarem |

Nenhuma decisão jurídica, compra, credencial ou publicação em `main` está autorizada.

## 2026-07-19 — versionar (ou não) o bundle embarcado `src/Harness.Host/wwwroot/**`

Observação: `src/Harness.Host/wwwroot/**` (build output do frontend, servido pelo Host) é
versionado, mas é regenerado por `tools/backend/build-frontend.sh` a cada `verify`/publish. Como a
Kimi publica telas (FR-1, FR-2, …) alterando apenas `frontend/**` e **não** o bundle, cada nova tela
deixa o bundle commitado stale e exige um re-embed pelo backend — churn recorrente de arquivos
gerados.

Suposição ativa / recomendação (não aplicada unilateralmente por tocar `.gitignore` compartilhado e o
fluxo de publish da Kimi): **gitignore de `src/Harness.Host/wwwroot/**` + `git rm --cached`**, deixando
o Host servir sempre o bundle recém-buildado (o publish self-contained já embarca o resultado de
`build-frontend.sh`). Decidir com o usuário/Kimi antes de aplicar. Enquanto não decidido, o backend
mantém o bundle consistente com o `frontend/**` de `origin/develop` a cada checkpoint de integração.

### RESOLVIDO 2026-07-19 — bundle `wwwroot` desversionado

Decisão (delegada pelo usuário ao backend, já que a Kimi atua majoritariamente no frontend):
**desversionar** `src/Harness.Host/wwwroot/**` (adicionado ao `.gitignore` + `git rm --cached`). O
bundle é build output puro, regenerado por `tools/backend/build-frontend.sh`, que roda em `verify.sh`
e no publish self-contained (F7). Encerra o churn recorrente a cada tela nova da Kimi (FR-N), que
alterava só `frontend/**` e deixava o bundle commitado stale. Pré-requisito consciente: rodar
`build-frontend.sh` (ou `verify.sh`) antes de `dotnet test` isolado ou de `dotnet run`, senão o Host
serve sem SPA (fallback API). O gate canônico `verify.sh` já garante isso.
