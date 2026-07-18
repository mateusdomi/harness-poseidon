# Evidência F2-PROV-1 — providers, roteamento e budgets

Data: 2026-07-18. Commit funcional publicado: `9e926c7` em `develop`.

A migration `0021_provider_catalog` cria providers, contas, modelos, políticas e budgets isolados por tenant. Contas guardam somente referência opaca para o cofre nativo; o contrato HTTP não contém segredo nem referência. O catálogo inicial fornece OpenAI, Anthropic e Ollama, quatro modelos, política segura e budgets global/por conta. Definições de agente apontam para modelos reais e handoff recusa modelo ausente ou desabilitado.

As cinco APIs list/read e os PATCH permitidos correspondem aos contratos TypeScript. Regras de roteamento exigem modelo ULID, fallback válido e custo positivo. O sync determinístico atualiza a fotografia sem rede em testes, retorna modelos e publica `quota.updated` para contas. Mudança de budget também publica quota, e toda mutação produz ledger + `audit.eventAppended`.

O cenário HTTP comprovou contagens/vínculos, ausência de segredo na resposta, edição de provider/model/routing/budget, sync, eventos globais de quota/auditoria e recuperação após restart. `tools/backend/verify.sh` passou com restore locked, format, build Release 0 warnings/0 errors e 151/151 testes (`Unit 91`, `Integration 27`, `Contract 20`, `Recovery 4`, `Architecture 6`, `Concurrency 3`). `frontend/**` e `docs/frontend/**` permaneceram sem alterações do backend.
