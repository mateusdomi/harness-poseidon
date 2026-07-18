# Evidência F2-TOOL-1 — catálogo e política de ferramentas

Data: 2026-07-18. Commit funcional publicado: `2185135` em `develop`.

A migration `0020_tool_catalog` cria skills, tools, plugins, vínculos plugin→tool e servidores MCP. O catálogo interno preserva schemas JSON de entrada/saída, teto de risk tier, checksum SHA-256, permissões e protocolo MCP estável `2025-11-25`; recursos do RC permanecem desligados. As seis definições canônicas passaram a referenciar skills e tools existentes.

As APIs list/read/PATCH de `skills`, `tools`, `plugins` e `mcp-servers` correspondem campo a campo aos schemas TypeScript. Mudança de tool publica `tool.statusChanged` global; toda alteração registra ledger e `audit.eventAppended`. O teste percorreu os quatro catálogos, validou todos os vínculos, alterou tool e MCP, observou os eventos e recuperou estado/endpoint após restart.

No módulo Tools, `ToolExecutionPolicy` nega componente desabilitado, ausência na allowlist da fase, escalada acima do teto da tool ou do risco aceito da tarefa e operação high/critical sem sandbox ou aceite inseguro. `PolicyCheckedToolExecutor<TInput,TOutput>` garante que a decisão ocorre antes da chamada tipada; o teste confirmou zero invocações em negação e uma invocação no sandbox.

`tools/backend/verify.sh` passou com restore locked, format, build Release 0 warnings/0 errors e 145/145 testes (`Unit 91`, `Integration 26`, `Contract 15`, `Recovery 4`, `Architecture 6`, `Concurrency 3`). `frontend/**` e `docs/frontend/**` permaneceram sem alterações do backend.
