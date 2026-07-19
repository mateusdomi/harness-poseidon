# Evidência F10-6 — modo servidor multiusuário, rate limit e carga de 30 usuários

Data: 2026-07-19.

## Entregas

- **`LocalProfileCreateCommand.JoinExistingTenant`** (padrão `false`, não-quebra): nos dois providers, o caminho de adesão valida a existência do tenant (`NotFound` se ausente) e pula o guard global de perfil único e o INSERT de tenant; o caminho de bootstrap permanece idêntico ao modo pessoal.
- **Serialização do bootstrap no PostgreSQL**: o lock consultivo do caminho de bootstrap passou a usar chave constante (`local-profile:bootstrap`) — antes era por tenant-id, o que não serializava duas criações concorrentes de primeiro perfil; adesões seguem com lock por tenant.
- **Endpoint de perfis ciente do modo servidor** (`HarnessServerOptions.Multiuser`): no modo servidor, o segundo perfil em diante adere ao tenant compartilhado; a corrida de bootstrap (dois "primeiros" simultâneos) é resolvida com retry como adesão ao tenant do vencedor. No modo pessoal SQLite o contrato 409 `profile_already_exists` permanece intacto.
- **Rate limiting no modo servidor**: `FixedWindowRateLimiter` particionado por IP remoto, janela de 1 minuto, limite configurável via `Harness:Server:RateLimitPermitsPerMinute` (padrão 600 no modo servidor; desligado no modo pessoal); excedente responde 429.

## Teste de carga e sonda adversarial (`PostgresMultiuserLoadTests`)

Host completo em modo servidor contra o container PostgreSQL gerenciado:

1. **30 usuários disparam a criação de perfil simultaneamente** — todos 201, 30 perfis distintos, e o banco termina com **exatamente 1 tenant** e 30 `local_users` (a corrida de bootstrap foi exercitada de verdade: 1 vencedor + 29 adesões via retry).
2. **30 organizações + 30 projetos criados concorrentemente** (um por usuário, com Chief provisionado por projeto) — todos 201.
3. **Sonda adversarial de isolamento**: sessão B tentando `PATCH` no perfil de A → 403; cookie forjado com ULID válido mas inexistente → 404; requisição sem cookie → 404.
4. **Rate limiter real**: marteladas em `/health` até observar **429** com limite de 2000/min.

Verde na primeira execução (~5s além do provisionamento do container).

## Failover de runner

Já coberto no PostgreSQL pela bateria de recuperação (`ProductionDurableExecutionRecoveryTests`): lease com fencing token, expiração de heartbeat e reaquisição por outro runner rodam contra os dois providers desde o F10-1; nenhum item novo era necessário aqui.

## Gate

Format sem mudanças; build Release zero warnings/erros; suíte integral **222/222** (nova: carga multiusuário). Restante do F10: RBAC/ABAC e, por último, **OIDC/Entra ID (única dependência externa — dados da app registration serão solicitados ao usuário nesse momento)**.
