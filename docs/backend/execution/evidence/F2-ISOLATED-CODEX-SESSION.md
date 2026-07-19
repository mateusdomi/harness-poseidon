# Evidência F2-DOGFOOD-1d.1 — sessão Codex isolada

Data: 2026-07-19. Commit funcional publicado: `5638840` em `develop`.

`ISandboxProvider` agora abre uma sessão de processo streaming, além do probe one-shot. A implementação Docker cria exclusivamente recursos `harness-` com `com.harness.managed=true`: rede interna sem egress, rede exclusiva do proxy, volumes de cache/estado e proxy obrigatório. O plano do agente monta somente a worktree em `/workspace`, usa rootfs read-only, scratch limitado, `no-new-privileges`, capabilities removidas e limites explícitos de CPU, memória e PIDs. `CODEX_HOME` reside no volume da tentativa; a home do usuário não é montada.

`CodexCliAppServerOptions` separa o diretório do processo no host do diretório apresentado ao protocolo dentro do container e aceita argumentos prefixos de launcher. Assim, o mesmo cliente app-server inicia por `docker run --interactive` e continua emitindo `cwd=/workspace` e `externalSandbox/restricted`. O encerramento fecha stdin antes do kill forçado, evitando que matar o cliente Docker deixe o container anexado vivo.

O teste integrado construiu uma imagem gerenciada, abriu proxy e sandbox reais e executou `CodexCliAgentExecutor` contra um app-server fixture dentro do container, sem rede externa nem cota. O turno estruturado retornou sessão/turno, gravou um marcador somente na worktree montada e terminou com zero container, network, volume ou imagem do Harness. O gerenciador Git ganhou remoção idempotente que recusa caminho fora da raiz controlada, destino não registrado e branch divergente.

`tools/backend/verify.sh` passou com frontend lint/typecheck/build e 270/270 testes; backend 176/176 (`Unit 96`, `Integration 39`, `Contract 28`, `Recovery 4`, `Architecture 6`, `Concurrency 3`), build Release com zero warnings/erros. `frontend/**` e `docs/frontend/**` permaneceram sem edição. Esta subfatia não encerra F2-DOGFOOD-1d: claims duráveis, catálogo transacional da tentativa/worktree e composição do projeto externo ainda são o próximo passo.
