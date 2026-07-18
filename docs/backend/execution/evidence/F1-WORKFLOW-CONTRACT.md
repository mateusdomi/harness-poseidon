# Evidência F1 — contrato de workflow, gates e progresso

- Executado em: 2026-07-18T15:37:49Z
- Incremento: EP-10a
- Resultado: verde

O módulo `Harness.Modules.Workflows` agora possui agregados provider-neutral para `WorkflowDefinition` e `WorkflowRun`. Definições têm IDs tipados, versões sequenciais, conteúdo copiado/imutável, hash SHA-256 determinístico e publicação idempotente. Somente uma versão publicada pode originar uma execução.

Cada versão define fases ordenadas, itens objetivos tipados (`Document`, `Task`, `Test`, `Gate`, `Approval`, `Evidence`), pesos positivos e gates com pré-requisitos explícitos. Um item `Gate` só avança pela avaliação do gate correspondente; chaves duplicadas, gate sem item pareado, pré-requisito externo/duplicado e estado mínimo pendente são recusados na criação da definição.

O `WorkflowRunAggregate` impõe:

- estados `Pending → Running ↔ Paused → Completed` e cancelamento terminal;
- uma única fase ativa e avanço estritamente sequencial;
- itens objetivos monotônicos `Pending → Executed → Validated → Approved`, sem saltos ou regressões;
- gate não contornável, com todos os pré-requisitos no nível mínimo definido;
- reavaliação explícita após gate falho;
- conclusão de fase somente com gates passados e nenhum item pendente;
- conclusão do run somente após a última fase.

`executed`, `validated` e `approved` nunca são informados por LLM ou comando. `WorkflowProgressCalculator` os recompõe sob demanda a partir dos pesos imutáveis e estados objetivos: cada dimensão soma somente os itens que alcançaram seu limiar, arredondando deterministicamente a duas casas. O cenário de duas fases comprovou progressos `30/30/0`, `40/40/10` após gate e `100/100/100` ao final.

Evidência: 9/9 testes focados verdes; `tools/backend/verify.sh` exit code 0; restore locked e format verdes; build Release 0 warnings/0 errors; suíte 83/83 (`Unit 55`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 12`).

Próximo incremento: EP-10b, migrations separadas SQLite/PostgreSQL para definição/versão/fases/itens/gates e runs, seguido de store transacional provider-neutral.
