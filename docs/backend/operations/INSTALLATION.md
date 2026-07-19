# Instalação do Harness Poseidon

## Escolha do modo

- **Pessoal**: Launcher + Host embarcado, SQLite e navegador local. Não requer IDE, Node nem .NET
  instalado na máquina de destino.
- **Servidor**: Host self-contained, PostgreSQL, até 30 usuários, OIDC opcional e execução atrás de
  proxy/reverse proxy administrado. O banco e o IdP são dependências operacionais externas.

Use somente artefatos produzidos de um commit validado por `tools/backend/verify.sh`.

## Pacote pessoal

No checkout de release:

```bash
tools/backend/publish-desktop.sh osx-arm64
```

Copie todo o conteúdo de `.artifacts/desktop/osx-arm64/` para um diretório dedicado e execute
`Harness.Launcher --no-browser` para smoke sem interface ou sem a opção para abrir o navegador. O
data dir padrão é `~/.harness-poseidon`; `--data-dir <caminho>` seleciona outro diretório. O pacote
nunca deve ser instalado dentro do data dir.

## Pacote servidor

```bash
tools/backend/publish-server.sh osx-arm64
```

O artefato fica em `.artifacts/server/osx-arm64/`. Configure antes do primeiro start:

| Chave | Obrigatória | Regra |
|---|---|---|
| `Harness__Database__Provider=postgres` | sim | seleciona a autoridade PostgreSQL |
| `Harness__Database__ConnectionString` | sim | segredo; injete pelo supervisor, nunca como argumento/linha de comando |
| `Harness__Auth__Oidc__Enabled` | não | `true` no modo Entra/OIDC |
| `Harness__Auth__Oidc__Authority` | com OIDC | issuer HTTPS exato do tenant |
| `Harness__Auth__Oidc__Audience` | com OIDC | Client ID/Application ID URI esperado |
| `Harness__Auth__Oidc__RequireHttpsMetadata` | recomendado | mantenha `true` fora do IdP fake local |

O operador deve fornecer a connection string por mecanismo de segredo do supervisor. Não a inclua
em shell history, argumentos, plist versionado, documentação ou log. O processo anterior de smoke
que prefixava segredo no comando foi encerrado e está registrado como R-013.

Depois de iniciar `Harness.Host --urls http://127.0.0.1:<porta>`, confirme `/health`. Exposição em
rede exige reverse proxy TLS e allowlist CORS explícita; o Host não deve escutar em interface pública
sem essa borda.

## Gate da instalação

```bash
tools/backend/verify-operations.sh
```

O gate valida sintaxe/permissões dos publicadores e exercita Launcher, Host PostgreSQL e
backup/restore reais. O smoke Entra ID real continua separado porque depende da app registration.
