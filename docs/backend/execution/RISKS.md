# Riscos ativos

| ID | Risco | Sinal/gatilho | Resposta | Estado |
|---|---|---|---|---|
| R-001 | contenção de escrita SQLite | `SQLITE_BUSY` não tratado na PoC-1 | dispatcher único; medir; ADR se exigir Postgres local opcional | monitorar |
| R-002 | Codex CLI instável para automação | drift do protocolo, falha de retomada ou heartbeat | app-server estável, checkpoints menores e reidratação por Git; fixar/validar versão antes de alternativa | mitigado/monitorar |
| R-003 | isolamento/quotas residuais do sandbox Docker | escape do daemon/VM ou crescimento do bind mount | rede interna + proxy allowlist; rootfs/scratch limitados; adicionar watchdog/budget da worktree na F1 | mitigado/monitorar |
| R-004 | drift com frontend em desenvolvimento | schemas/nome de evento divergem | OpenAPI/eventos canônicos + reconciliação explícita; nunca editar frontend | ativo |
| R-005 | SDK .NET 10 ausente globalmente | bootstrap `net10.0` não compila fora do wrapper | SDK 10.0.302 local e instalador idempotente | mitigado |
| R-006 | pushes concorrentes em `develop` | push rejeitado ou rebase conflita | fetch/rebase/teste novamente; preservar integralmente conteúdo Kimi | ativo |
| R-007 | EF Core SQLite traz native SQLite vulnerável | restore emite `NU1903` GHSA-2m69-gcr7-jv3q | pin central `SQLitePCLRaw.lib.e_sqlite3` 3.53.3, lockfiles e audit obrigatório | mitigado |
| R-008 | gerador OpenAPI traz parser vulnerável | restore emite `NU1903` GHSA-v5pm-xwqc-g5wc | pin central `Microsoft.OpenApi` 2.7.5, primeira versão 2.x corrigida; audit obrigatório | mitigado |
| R-009 | imagem oficial PostgreSQL contém runtime Go crítico | Docker Scout detecta CVE-2025-68121 no `gosu` da base oficial | imagem Harness mínima sobre Alpine 3.24 com PostgreSQL 18/su-exec; exigir zero crítica/alta/média e monitorar 2 baixas + 1 não classificada em libxml2 sem fix disponível | mitigado/monitorar |
| R-010 | SDK local encerra restore abruptamente | `dotnet restore --force-evaluate` terminou uma vez com exit 139 sem diagnóstico | repetir isoladamente com verbosity; recorrência exige crash dump antes de editar; restore subsequente, build e gate completo passaram | monitorar |
| R-011 | dependências de tooling frontend com avisos de auditoria | auditoria completa reporta 8 vulnerabilidades em Vitest/Storybook e transitivas; `npm audit --omit=dev` reporta zero | não alterar `frontend/**`; manter produção sem achados e coordenar upgrade major do tooling na frente proprietária | monitorar |
| R-012 | homologação visual sem navegador conectado | runtime de browser retornou lista vazia, impedindo validação humana assistida | preservar evidência HTTP/publish; repetir navegação visual quando um browser estiver disponível; não promover GNG-3 antes disso | ativo |
| R-013 | token do bot Telegram exposto na linha de comando de um shell supervisor herdado | inventário de processos mostrou a atribuição do segredo no comando do processo pai, embora Git/banco/log do Host permaneçam limpos | árvore encerrada; rotacionar o token no BotFather antes do próximo smoke e injetá-lo por secret store/arquivo de configuração protegido, nunca por comando visível em `ps` | ativo — rotação externa pendente |
| R-014 | rule comunitário Semgrep C# SQL produz 85 falsos positivos na DAL parametrizada | `p/csharp` marca qualquer atribuição indireta/interpolação de constantes a `CommandText` sem análise de fluxo suficiente | excluir somente o check id ruidoso; manter os outros 26 rules comunitários e 4 regras locais, incluindo proibição de SQL interpolado fora da fronteira de persistência/migração; reavaliar quando a DAL mudar | mitigado/monitorar |

Não há No-Go registrado. GNG-1 e GNG-2 estão verdes; R-012 mantém GNG-3 em execução sem bloquear trabalho independente.
