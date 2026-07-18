# Evidência F0 PoC-3 — lease e fencing token

- Executado em: 2026-07-18T12:08:18Z
- Ambiente: macOS arm64, .NET SDK 10.0.302, SQLite WAL
- Resultado: verde

## Cenário executado

1. `owner-a` adquiriu `project-chief` com fencing token 1 e lease de 10 s.
2. `owner-b` tentou adquirir após 5 s e foi bloqueado porque o lease ainda estava válido.
3. Após 11 s, `owner-b` adquiriu atomicamente com fencing token 2.
4. `owner-a` tentou gravar `stale-value` com token 1: zero linhas afetadas.
5. `owner-a` tentou renovar token 1: zero linhas afetadas.
6. `owner-b` gravou `current-value` e renovou token 2 com sucesso.

Estado final: owner B, fencing token 2, valor `current-value` e expiração renovada correta. A escrita/renovação do owner antigo foi rejeitada pelo predicado transacional `(resource, owner, fencingToken, expiresAt)`.

## Comandos e saídas

```bash
tools/backend/dotnet.sh build tests/Harness.ConcurrencyTests/Harness.ConcurrencyTests.csproj --configuration Release --no-restore
tools/backend/dotnet.sh test tests/Harness.ConcurrencyTests/Harness.ConcurrencyTests.csproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~LeaseFencingPocTests --logger 'console;verbosity=normal'
```

Exit codes: 0. Build: 0 warnings/0 errors. Execução inicial: 1/1 teste aprovado (`ExpiredOwnerCannotWriteOrRenewAfterNewFencingTokenIsIssued`) em 20 ms.

O teste foi repetido cinco vezes adicionais, sempre 1/1 aprovado (22–23 ms). Depois, `tools/backend/verify.sh` terminou com exit code 0 e 35 testes verdes nas seis suítes. O cleanup deixou zero bancos/WAL/SHM da PoC.

Esta PoC valida a invariante; a abstração/implementação completa de leases do motor durável entra na Fase 1.
