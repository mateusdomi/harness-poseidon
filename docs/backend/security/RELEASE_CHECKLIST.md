# Checklist de release 1.0 e DoD global

Atualizado em: 2026-07-20. Este checklist separa gates automatizados de homologação humana. Um item só fica verde com evidência reproduzível; criar o arquivo ou executar parte do fluxo não equivale a aceite.

## Release candidate técnico

| Critério | Estado | Evidência/gate |
|---|---|---|
| Build reproduzível, format, regressão backend/frontend | verde | `tools/backend/verify.sh`; contagens reais ficam no `TEST_REPORT.md` da RC |
| SAST dedicado + analisadores .NET | verde | `tools/backend/verify-sast.sh`; Semgrep 1.170.0 non-root, 30 regras, zero achado; CA/IDE com warning-as-error |
| Dependency audit e SBOM | verde | `NuGetAudit=all` no restore; `npm audit --omit=dev` anteriormente zero; `tools/backend/sbom.sh`; R-011 cobre tooling frontend |
| Secret scanning | verde | `tools/backend/scan-secrets.sh` + `SecretHygieneTests` |
| Threat model, headers, cookies e CORS | verde | `THREAT_MODEL.md`, F11-2 |
| Upgrade, backup/restore, migração e licença | verde | `verify-resilience.sh`, F8, F11-1/4/5 |
| Recuperação, fencing, isolamento e carga 30 usuários | verde | `verify-resilience.sh`, GNG-2/GNG-5 |
| Publish pessoal/servidor, update/uninstall e runbooks | verde | `verify-operations.sh`, F7-2, F11-6 |
| OpenAPI/eventos reconciliados | verde | ContractTests no gate integral; OpenAPI publicado |
| Governance P2 sem auto-promoção | verde | `GOVERNANCE-GATE-P2.md`; lifecycle provider-neutral SQLite/PostgreSQL + API real |
| Comando único e pacote self-contained | verde | `./poseidon`; instalação/start/status/E2E real/restart/stop/doctor no gerador de RC |
| Docker sem órfãos gerenciados | verde | inventário final do `verify-release-candidate.sh` |

O agregador técnico é `./poseidon release-candidate`. Ele exige `develop` e working tree limpa,
gera os artefatos verificáveis e não fabrica evidência visual nem converte execução automática em
homologação.

## Itens ainda necessários para GNG-6/DoD global

| Critério | Estado | Condição de fechamento |
|---|---|---|
| E2E do frontend contra API real | gate técnico incluído | `./poseidon release-candidate`; aceite visual continua no GNG-3 |
| Acessibilidade WCAG/axe | gate técnico incluído | Playwright/axe no gerador de RC; aceite humano continua no GNG-3/GNG-6 |
| GNG-3 dogfood visual | pendente humano | aceite visual explícito em navegador, sem inferência por HTTP |
| GNG-4 instalação limpa | pendente humano/credencial | macOS limpo, pacote Developer ID/notarizado, licença offline e dados legíveis pós-expiração |
| GNG-6 aceite final | pendente humano | revisar este checklist, riscos ativos e evidências; registrar decisão explícita |

Smokes Entra ID, Teams e executor/modelo reais continuam isolados como dependências de credenciais. Eles não devem impedir correções técnicas independentes, mas a decisão de release deve declarar se são obrigatórios para o ambiente alvo.
