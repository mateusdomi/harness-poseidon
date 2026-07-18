# Reconciliação de contratos

## Estado inicial — 2026-07-18

O contrato provisório do frontend foi lido sem modificação. As convenções globais coincidem: `/api/v1`, ULID string, camelCase, cursor, RFC 7807, UTC ISO-8601, cookie local e hub `/hubs/events` com sequência por stream.

### Divergências conhecidas

| Tema | Missão backend v1.3 | Frontend FE-0 | Tratamento planejado |
|---|---|---|---|
| publicação de workflow | `workflow.definitionPublished` | `workflow.versionPublished` | backend segue o nome v1.3; avaliar alias temporário documentado após contrato real |
| catálogo de ferramenta | `tool.catalogChanged` | `tool.statusChanged` | modelar evento canônico v1.3; reconciliar payload com a Kimi |
| protótipo criado | `prototype.created` | ausente | publicar no catálogo backend e adicionar teste de drift |
| decisão humana | `decision.requested/resolved` | ausência de recurso/evento explícito; frontend usa `approvals` | manter recursos distintos `approval-requests` e `human-decisions` como exige v1.3 |
| definição publicada | evento exige recurso `workflow-definitions` | frontend usa `workflow-templates`, `workflow-versions`, `workflows` | oferecer contrato canônico e mapear sem quebrar telas quando a integração começar |
| documentos órfãos | obrigatório no backend | não explícito no registro FE-0 | criar endpoint canônico na fase apropriada |

### Regra de trabalho

- `docs/contracts/openapi.json` é gerado pelo Host e bloqueado por drift test desde a PoC-7.
- `docs/contracts/events.json` é publicado a partir de `EventTypeCatalog` e bloqueado por drift test desde a PoC-7.
- Testes de contract drift compararão os artefatos gerados e as convenções provisórias sem escrever no frontend.
- Divergências nunca serão resolvidas apagando ou alterando silenciosamente contratos da Kimi.

## Atualização FE-1 — 2026-07-18

O rebase incorporou FE-1a/1b/1c e o handoff passou de 201 para 203 linhas. Mudanças lidas e preservadas:

- `POST /tasks/<id>/priority` com `{ priority }`: compatível como comando explícito de domínio, sem abrir PATCH genérico em tarefas; deve entrar no contrato backend da Fase 2.
- `Organization` ganhou marca, policies e templates herdáveis; alinha-se ao requisito de identidade visual/políticas.
- `Project` ganhou state, criticality, provider/default branch, tecnologias, marca, membros, `configVersion` e `lastActivityAt`; backend deve preservar versionamento de configuração.
- `Settings` ganhou `workingDirectory` e `unsafeModeAcceptedAt`; o segundo campo corresponde ao aceite explícito exigido para fallback sem sandbox.
- O gatilho de chat `[plan]` é cenário exclusivo de mock/E2E e o próprio handoff declara que não é contrato backend; não será implementado como comando mágico.

## Atualização PoC-7 — 2026-07-18

O contrato backend canônico agora materializa `/hubs/events`, método de assinatura `Subscribe`, método cliente `event`, envelope completo e `GET /api/v1/event-streams/snapshot`. O catálogo segue estritamente a missão v1.3, portanto as divergências `workflow.versionPublished`/`workflow.definitionPublished` e `tool.statusChanged`/`tool.catalogChanged` permanecem explícitas e ainda não ganharam aliases. Nenhum arquivo do frontend foi modificado.

## Atualização F1-DOC-1 — 2026-07-18

Os contratos FE-2 de `Document`, `DocumentVersion`, `Approval`, comandos e handoff foram lidos e preservados. O modelo de fundação adotou:

- conteúdo de versão fora do banco, catalogado por caminho relativo e SHA-256, conforme a missão; a API futura resolve `body` pelo catálogo/filesystem;
- `phaseName = null` como órfão e classificação como metadado separado de conteúdo, compatível com D-029;
- estados persistidos em snake_case e mapeamento camelCase (`in_elaboration` ↔ `inElaboration`) somente na borda HTTP;
- entidade canônica `DocumentApprovalRequest`, alinhada ao recurso obrigatório `approval-requests`; na integração, a UI poderá receber projeção compatível em `approvals` sem fundir decisões humanas distintas;
- resolução aprovada leva `awaitingApproval→approved`; rejeição com nota obrigatória leva `awaitingApproval→inElaboration`, como exige o handoff;
- `inconsistent` foi modelado; waiver permanece para a fatia de prototipação/governança prevista na missão e não foi fabricado antecipadamente.

Nenhum contrato do frontend foi editado. Eventos públicos continuam os canônicos `document.stateChanged`, `approval.requested` e `approval.resolved`.

## Entrada F2 e perfil local — 2026-07-18

Todos os 13 arquivos em `frontend/src/api/contracts/**`, `docs/frontend/CURRENT_STATE.md` e `HANDOFF_API.md` foram relidos após fetch/rebase. Os tree hashes protegidos permaneceram `frontend=87552e2b...` e `docs/frontend=8294e1ce...`.

A primeira fatia vertical materializou o schema `Profile` exatamente com `id`, `displayName`, `email`, `avatarUrl`, `locale`, `createdAt` e `lastActiveAt`; a versão OCC permanece interna e não vaza na resposta. `profiles/current`, paginação, POST de onboarding e PATCH foram publicados no OpenAPI.

O quadro do handoff marca criação de `profiles` como indisponível, mas `CreateInputMap` — fonte tipada declarada pelo próprio frontend — inclui criação e a UI de onboarding depende dela. O backend implementou o POST e registrou o desvio documental, sem alterar arquivos da Kimi. Não foi inventado evento `profile.updated`, pois ele não existe nem no catálogo normativo v1.3 nem no catálogo TypeScript; auditoria/evento público será ligado à fatia de governança sem criar drift.
