# Evidência F2-RUN-1 — run targets .NET e Node

Data: 2026-07-18. Commit funcional publicado após rebase: `4df5854` em `develop`.

O detector percorre somente a raiz autorizada do projeto, com profundidade e quantidade de arquivos limitadas, sem seguir reparse points e ignorando diretórios de build, dependências e metadados. Projetos `.csproj` e `package.json` compatíveis originam targets persistidos com identidade estável, porta loopback reservada e detalhes de lançamento mantidos fora do contrato público.

O supervisor inicia processos reais usando `ArgumentList`, sem shell, e injeta somente o ambiente necessário. Start, stop e restart atuam exclusivamente sobre processos gerenciados; stdout/stderr são capturados como `run.logAppended`; shutdown encerra a árvore inteira; restart do Host preserva os targets e reconcilia estado sem alegar que um processo órfão continua supervisionado. O cleanup por projeto para todas as árvores gerenciadas e registra `run.environmentCleaned` no ledger e no stream global de auditoria.

O cenário HTTP criou em runtime uma aplicação .NET 10 e um servidor Node, detectou ambos, iniciou-os e validou respostas HTTP reais, reiniciou Node, parou .NET, executou cleanup, verificou logs no stream do projeto, auditoria global e recuperação dos mesmos IDs após restart. A API exige perfil local e aceite explícito de modo inseguro antes de executar código do workspace. OpenAPI/drift coincidem com os nove campos públicos de `RunTarget` e cobrem list/get/start/stop/restart/cleanup sem expor executável ou argumentos.

`tools/backend/verify.sh` passou antes e depois do rebase concorrente sobre `00783f8`, com restore locked, format, build Release 0 warnings/0 errors e 161/161 testes (`Unit 91`, `Integration 31`, `Contract 26`, `Recovery 4`, `Architecture 6`, `Concurrency 3`). SQLite ficou `25→0`, PostgreSQL `11→0`; `frontend/**` e `docs/frontend/**` permaneceram sem edição backend.
