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

- `docs/contracts/openapi.json` será gerado pelo Host quando os primeiros endpoints existirem.
- `docs/contracts/events.json` será publicado a partir de uma fonte tipada do backend.
- Testes de contract drift compararão os artefatos gerados e as convenções provisórias sem escrever no frontend.
- Divergências nunca serão resolvidas apagando ou alterando silenciosamente contratos da Kimi.

## Atualização FE-1 — 2026-07-18

O rebase incorporou FE-1a/1b/1c e o handoff passou de 201 para 203 linhas. Mudanças lidas e preservadas:

- `POST /tasks/<id>/priority` com `{ priority }`: compatível como comando explícito de domínio, sem abrir PATCH genérico em tarefas; deve entrar no contrato backend da Fase 2.
- `Organization` ganhou marca, policies e templates herdáveis; alinha-se ao requisito de identidade visual/políticas.
- `Project` ganhou state, criticality, provider/default branch, tecnologias, marca, membros, `configVersion` e `lastActivityAt`; backend deve preservar versionamento de configuração.
- `Settings` ganhou `workingDirectory` e `unsafeModeAcceptedAt`; o segundo campo corresponde ao aceite explícito exigido para fallback sem sandbox.
- O gatilho de chat `[plan]` é cenário exclusivo de mock/E2E e o próprio handoff declara que não é contrato backend; não será implementado como comando mágico.
