# Evidência F2-DOGFOOD-1a — execução estruturada de agentes

Data: 2026-07-18. Commit funcional publicado: `5a2a1be` em `develop`.

Foi criada a borda `IAgentExecutor` com request/result independentes de provider. `FakeAgentExecutor` é determinístico e permanece a única implementação registrada pelo Host durante testes automatizados. O endpoint de turno deixou de compor a resposta diretamente: ele recompõe o `StatusDigest`, chama a interface, valida a saída do Chief e só então persiste o turno já existente.

`CodexCliAgentExecutor` usa a integração profunda do app-server, validada contra a documentação oficial e o JSON Schema gerado pela `codex-cli 0.144.5`: `initialize`, `thread/start|resume`, `turn/start`, deltas `item/agentMessage/delta`, item final e `turn/completed`. O cliente correlaciona um turno por thread, propaga encerramento inesperado, retoma sessão quando fornecida e solicita `outputSchema`. Se a primeira mensagem final violar o contrato, executa exatamente um turno de repair e valida novamente.

O contrato `ChiefTurnOutput` recusa JSON não objeto, campos desconhecidos, textos fora de bounds, mais de 20 demandas, risk tier fora do conjunto fechado e critérios ausentes ou excessivos. Demandas são propostas estruturadas; não atualizam o banco diretamente. O adaptador Codex falha no construtor se a composição não comprovar rootfs read-only, worktree isolada, egress restrito e limites de recursos, impedindo uso acidental fora do sandbox. O Host padrão ainda usa Fake até a próxima composição Docker.

O teste de protocolo usou um app-server fixture sem rede/cota: a primeira resposta foi deliberadamente inválida, o executor enviou o repair na mesma thread e aceitou a segunda resposta estrita, preservando IDs e deltas. Três testes unitários cobrem determinismo, validação e fail-closed do sandbox. `tools/backend/verify.sh` passou com 270/270 testes frontend e 173/173 backend (`Unit 96`, `Integration 36`, `Contract 28`, `Recovery 4`, `Architecture 6`, `Concurrency 3`), build Release com zero warnings/erros. `frontend/**` e `docs/frontend/**` permaneceram sem edição backend.
