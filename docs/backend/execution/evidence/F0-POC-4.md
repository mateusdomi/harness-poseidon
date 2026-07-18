# Evidência F0 PoC-4 — subprocesso Codex CLI, heartbeat e retomada

- Executado em: 2026-07-18T12:21:38Z
- Ambiente: macOS arm64, .NET SDK 10.0.302, Codex CLI 0.144.5, Git 2.50.1
- Resultado: verde

## Cenário executado

1. Um repositório Git fixture descartável foi criado sob o diretório de artefatos do teste, somente com branch `main`, e recebeu um commit de checkpoint.
2. O wrapper iniciou `codex app-server --listen stdio:// --strict-config` como subprocesso real, comunicando-se por JSON-RPC sobre stdio.
3. O ambiente do subprocesso foi reconstruído por allowlist e recebeu um diretório `CODEX_HOME` exclusivo, contido no artefato da tentativa. Nenhuma configuração, sessão ou credencial pessoal foi herdada.
4. Uma thread persistente foi criada em sandbox `read-only`, com aprovação `never`. `thread/inject_items` materializou um marcador de checkpoint sem iniciar turno de modelo, usar rede ou consumir cota.
5. O supervisor emitiu heartbeats com PID, sequência estritamente crescente e timestamp UTC.
6. O processo foi interrompido com kill da árvore exata; o PID deixou de existir e nenhum `codex app-server` órfão permaneceu.
7. Um segundo processo, iniciado sobre o mesmo estado isolado, retomou a mesma thread por `thread/resume` e preservou o `threadId`.
8. Independente da sessão do provider, o commit exato e a limpeza da working tree foram reconstruídos pelo Git. Um terceiro processo iniciou uma nova thread efêmera com instrução de reidratação contendo o SHA do checkpoint.
9. Todos os diretórios de estado, rollouts e repositórios fixture foram removidos no `finally` do teste.

O primeiro ensaio mostrou que `thread/start` sozinho ainda não materializa rollout; a retomada retornou `no rollout found`. A hipótese foi instrumentada com o schema gerado pela própria CLI. A correção passou a persistir um item de marcador pelo método estável `thread/inject_items`, sem chamada ao modelo. O ensaio seguinte e cinco repetições adicionais ficaram verdes.

## Comandos e saídas

```bash
tools/backend/dotnet.sh build tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj -c Release --no-restore
tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~CodexCliAgentExecutorPocTests --logger 'console;verbosity=detailed'
```

Exit codes: 0. Build: 0 warnings/0 errors. Execução validada: 1/1 teste aprovado em 683 ms. Cinco repetições adicionais: 1/1 aprovada em cada execução (577–653 ms). Depois, `tools/backend/verify.sh` terminou com exit code 0 e 36 testes verdes nas seis suítes. Verificações posteriores encontraram zero processos `codex app-server` e zero artefatos remanescentes da PoC.

Nenhum turno real de modelo integra o gate automatizado. Quando adicionado, esse smoke exigirá `HARNESS_RUN_REAL_AGENT_TESTS=true` e credencial explicitamente fornecida ao proxy de credenciais, nunca herdada pelo ambiente do agente.
