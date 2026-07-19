# Evidência F2-FE-1 — integração reproduzível do frontend

Data: 2026-07-18. Commit funcional publicado: `c117a2f` em `develop`.

`tools/backend/build-frontend.sh` trata `frontend/**` como entrada somente leitura: cria uma cópia confinada em `.artifacts`, instala exatamente o lockfile com `npm ci`, executa lint, typecheck e testes, e gera um bundle HTTP same-origin. Os 127 arquivos resultantes, com 2,6 MiB, ficam versionados em `src/Harness.Host/wwwroot`; não surgiram `node_modules` nem `dist` na árvore protegida. `tools/backend/dev-frontend.sh` e a configuração Vite associada oferecem o mesmo isolamento para desenvolvimento e encaminham `/api` e `/hubs` ao Host real.

O `Harness.Host` resolve o bundle tanto na árvore de fontes quanto no publish, serve arquivos estáticos e `index.html` para rotas SPA. O fallback exclui `/api/**` e `/hubs/**`, de modo que uma API inexistente continua 404 e nunca vira HTML. O teste de integração abriu `/`, `/settings`, um asset JavaScript real e uma rota API ausente, além de verificar que a factory compilada não fixa `http://localhost:5001`. O publish Release terminou com sucesso e continha `wwwroot/index.html` e os assets.

O gate combinado `tools/backend/verify.sh` passou com lint, typecheck, build Vite e 270/270 testes frontend; no backend, restore locked, format, build Release com zero warnings/erros e 169/169 testes (`Unit 93`, `Integration 35`, `Contract 28`, `Recovery 4`, `Architecture 6`, `Concurrency 3`). `npm audit --omit=dev` retornou zero vulnerabilidades. A auditoria completa encontrou oito achados apenas no tooling de desenvolvimento, registrados como R-011 porque a atualização major pertence à árvore frontend protegida.

O smoke HTTP usando Host e banco reais passou, mas o runtime de navegador desta sessão retornou zero browsers disponíveis. Assim, a integração é verde técnica, enquanto a homologação visual/humana permanece explicitamente pendente em R-012 e o GNG-3 continua em execução. `frontend/**` e `docs/frontend/**` permaneceram integralmente sem edição backend.
