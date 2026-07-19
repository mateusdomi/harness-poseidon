# V3 — histórico consultável das definições de agentes

Data: 2026-07-19

## Resultado

Os snapshots imutáveis que já eram gravados a cada criação/edição agora possuem leitura HTTP
tipada em `GET /api/v1/agent-definitions/{definitionId}/versions`. A resposta contém versão,
ator, data e snapshot estrutural completo; consumidores não precisam interpretar `snapshot_json`.

A listagem usa ordem `version DESC`, `limit` até 200 e cursor exclusivo `beforeVersion`. A
consulta valida sessão e tenant antes de ler o histórico, inclusive quando outro tenant conhece o
ULID da definição.

## Evidência executada

- behavior provider-neutral lê v2 e v1 separadamente e compara os nomes preservados;
- teste HTTP comprova snapshot, ator e paginação v2→v1;
- OpenAPI publica rota e contratos `AgentDefinitionVersion*` fortemente tipados;
- nenhuma migration nova foi necessária: a autoridade continua sendo
  `agent_definition_versions` criada em 0040;
- nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi alterado.
