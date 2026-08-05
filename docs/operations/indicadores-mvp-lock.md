# Indicadores TrensRJ — MVP Lock e normalização do levantamento (2026-08-05)

**Projeto:** Indicadores TrensRJ (nome provisório) · `01KZ9MWXYXJZCJ0VAMWQHAQHDC` · org TrensRJ ·
**Prazo:** 2026-08-20 · criticidade critical · estado `paused`.
**Fonte:** `Levantamento_Indicadores_TrensRJ.txt` (1.194 linhas, sha `D42430AD…`), anexado como
`requirements_source` à solicitação `01KZ9N4M8NZMQ79F9VYYJ6CA51`.
**Provas executáveis:** `IndicadoresDocumentTests` + `DocumentAgnosticIntakeTests`.

## Fronteira do MVP (scope lock de 20/08 — a seção "ENTREGA DO MVP" do próprio documento)

Login · gestão de usuários · perfis · permissões · cadastro de áreas · páginas · indicadores ·
configuração de modelos · download de planilha modelo · importação XLSX/CSV · validação · prévia ·
confirmação · histórico de cargas · dashboard · filtros · auditoria · responsividade · Oracle ·
dados de demonstração · Docker · documentação de instalação. **O MVP deve ser executável.**

## O que NÃO entra no prazo de 20/08 (classificado, com vínculo)

| Item do documento | Vínculo | Regra |
|---|---|---|
| Entra ID / AD / LDAP / SSO / MFA | **Future** | "futura integração" — arquitetura preparada, nada implementado |
| XLS/JSON/XML/API/SFTP/agendado | **Future** | "suportar futuramente" |
| Kubernetes | **Future** | "como evolução" |
| E-mail/Teams/WhatsApp/Webhook (notificações) | **Future** | "integração futura" |
| Editor drag-and-drop de dashboard | **Optional** | "avaliar" — decisão aberta; nunca card por default |
| Entrega Final (seção 28: pipeline completo, backup, rollback, manuais, monitoração plena) | **Pós-MVP** | após validação do MVP |

Nenhuma capacidade Future/Optional gera card de implementação do MVP por default — imposto pelo
vínculo do `SourceKnowledgeClassifier` (testes congelados).

## Critérios de aceite (a seção de aceite do documento, extraída por título — não por número)

**22 critérios**, ids estáveis por conteúdo (sobrevivem a renumeração/retitulação — provado),
representados no grafo do projeto como Requirements (`acceptance_criterion`).

Coverage preview: **Total 22 · Covered 0 · Lost 0 · Awaiting planning 22.** Correto pré-Planning.

Conflito MVP × critérios: os 22 aplicam-se ao MVP quando cobrem funcionalidades da fronteira
acima; qualquer tensão identificada no Planning é resolvida pela Bruna por autoridade e escopo
(não aqui).

## Normalização — o que o documento DIZ vs o que GOVERNA

| Afirmação (literal) | Categoria | Vínculo | Efeito |
|---|---|---|---|
| "Utilizar banco de dados Oracle" | Constraint | Required | **única diretiva de perfil do documento inteiro** (provado) |
| "Sugestão de stack: React/Vite/Next.js" | TechnicalPreference | Preferred | informa; baseline resolve |
| "Escolher: Java / C# / Node" · "Dar preferência para Java ou C#" | TechnicalPreference | Preferred | **zero** diretivas — o baseline (.NET) decide |
| "Não criar gráficos fixos por indicador" | Requirement | **Required** | invariante do motor configurável (canon provided-artifacts) |
| "Atue como equipe…", 7 fases, "aguarde validação" | ProcessInstruction | **NonAuthoritative** | não altera Playbook nem autonomia |

## Effective Profile preview (resolvido do documento real, com proveniência)

Web (sistema web responsivo) · Frontend obrigatório **React** (baseline) · Backend **.NET**
(baseline — Java aparece no texto e NÃO decide) · **Oracle** (override, autoridade
ProjectRequirement, fonte: o levantamento) · REST + OpenAPI required · Deadline 20/08 · critical.

## NFRs (classificados; nada inventado)

| NFR | Classe |
|---|---|
| Volumes de teste 10k/100k/1M registros | **ANSWERED** (§ desempenho) |
| Cargas simultâneas / async / filas / cache / pool / timeout / limite de concorrência | **ANSWERED** (requisito existe) |
| Usuários simultâneos esperados | **NON_BLOCKING_UNKNOWN** — perguntar antes da fase de performance |
| RTO / RPO | **NON_BLOCKING_UNKNOWN** |
| Alvo de disponibilidade | **NON_BLOCKING_UNKNOWN** |
| Cobertura mínima 80% em serviços críticos | **ANSWERED** |

**BLOCKING: nenhum.** Discovery/Architecture podem começar; a lista acima vira ASK no ponto de
decisão em que cada item realmente importa.

## Artefatos do projeto

| Arquivo | Papel | Observação |
|---|---|---|
| Levantamento_Indicadores_TrensRJ.txt | `requirements_source` | fonte de conhecimento — nunca system prompt |
| Dashboard_BPMN_TrensRJ_24-07-2026.html | `design_reference` | exemplo a representar pela configuração genérica |
| licencas_erp_dashboard.html | `design_reference` | contém 760 registros reais de usuário: contexto de modelo recebe versão SANITIZADA (determinística); original intacto com hash |

Se um protótipo React (ZIP) for produzido no Lovable para este sistema, ELE passa a ser o
`provided_frontend`; os HTMLs continuam referência.
