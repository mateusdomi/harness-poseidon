# F2-WF-1a — catálogo, vínculo e run de workflow

Data: 2026-07-18

## Escopo comprovado

- O Host registra `IWorkflowStore`/`SqliteWorkflowStore`; a autoridade F1 de definições, runs, gates e progresso não foi duplicada.
- A migration SQLite `0014_workflow_catalog_projection` acrescenta descrição do template, vínculo único por projeto, aceites de risco, referência do run ao workflow e metadados de decisão do gate.
- APIs tenant-scoped expõem list/read de `workflow-templates`, `workflow-versions`, `workflows`, `workflow-runs`, `phases` e `gates` nos campos exatos do TypeScript.
- Criação de template expande o contrato simples `phases + gatesByPhase` em hierarquia F1 válida de fases, objetivos, gate-objectives e requisitos.
- Criação de vínculo valida projeto/versão/perfil, registra o aceite de risco e recusa um segundo workflow para o mesmo projeto.
- Criação de run materializa todas as projeções e aplica `Start`, produzindo `running` e exatamente uma fase ativa.
- `workflow.versionPublished` usa o payload canônico `{ templateId, versionId, version }` e o stream global.

## Cenário executado

O teste HTTP real criou perfil, organização e projeto, publicou um template com duas fases e gate de qualidade, vinculou-o em modo manual, registrou aceite humano e iniciou um run. As leituras filtradas retornaram duas fases, uma ativa e um gate pendente. Um vínculo duplicado retornou `409`. Após restart do Host no mesmo SQLite, workflow e run continuaram legíveis e o run permaneceu `running`.

## Gates

- `tools/backend/verify.sh`: exit code 0.
- Build Release: zero warnings e zero erros.
- Unit: 84/84.
- Integration: 23/23.
- Contract: 9/9.
- Recovery: 4/4.
- Architecture: 6/6.
- Concurrency: 3/3.
- Total: 129/129.
- SQLite migrations: `14→0`; PostgreSQL: `9→0`.
- `frontend/**` e `docs/frontend/**`: nenhuma alteração.
