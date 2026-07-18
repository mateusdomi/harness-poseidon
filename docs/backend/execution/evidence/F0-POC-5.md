# Evidência F0 PoC-5 — branches, worktrees e claims em fixtures

- Executado em: 2026-07-18T12:28:53Z
- Ambiente: macOS arm64, .NET SDK 10.0.302, Git 2.50.1
- Resultado: verde

## Cenário executado

Três repositórios Git descartáveis foram inicializados sob os artefatos do teste, cada um exclusivamente com branch inicial `main`. Em cada fixture:

1. Duas solicitações concorrentes criaram `task/a` e `task/b` e worktrees exclusivas para `attempt-a` e `attempt-b`.
2. A mutação dos metadados do repositório foi serializada internamente; os commits nas worktrees independentes ocorreram em paralelo.
3. Repetir a criação de `(task/a, destino-a)` retornou o mesmo descritor sem duplicar branch/worktree.
4. O estado observado foi exatamente três branches (`main`, `task/a`, `task/b`) e três worktrees (raiz + duas tentativas).

Matriz de claims:

| Fixture | Claim A | Claim B | Resultado |
|---|---|---|---|
| `disjoint-code` | `src/api/**` | `src/runner/**` | ambos adquiridos; dois commits paralelos |
| `intersecting-code` | `src/payments/**` | `src/payments/checkout.cs` | segundo rejeitado antes da escrita; conflito aponta `attempt-a` |
| `disjoint-docs` | `docs/specs/**` | `docs/runbooks/**` | ambos adquiridos; dois commits paralelos |

A comparação é por segmento: `src/api/**` não colide com `src/api-client/**`. A aquisição repetida do mesmo claim pelo mesmo attempt é idempotente; após release, outro attempt pode adquirir o escopo.

O teste fotografou refs locais e `git worktree list --porcelain` do repositório oficial antes/depois. Em todas as execuções, ele permaneceu somente com `main` e `develop`, uma worktree em `develop` e nenhum arquivo alterado pela fixture. O cleanup removeu integralmente os três repositórios e suas seis worktrees de tentativa.

## Comandos e saídas

```bash
tools/backend/dotnet.sh test tests/Harness.UnitTests/Harness.UnitTests.csproj -c Release --no-build --filter FullyQualifiedName~ScopeClaimRegistryTests --logger 'console;verbosity=detailed'
tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~GitWorktreeClaimsPocTests --logger 'console;verbosity=detailed'
```

Exit codes: 0. Claims: 5/5 testes aprovados. Integração: 1/1 aprovada em 1,3 s; cinco repetições adicionais ficaram verdes em aproximadamente 1 s cada. Depois, `tools/backend/verify.sh` terminou com exit code 0 e 42 testes verdes nas seis suítes. Após as repetições, `git branch` mostrou apenas `develop`/`main`, `git worktree list` mostrou somente a raiz oficial e não restou artefato da PoC-5.
