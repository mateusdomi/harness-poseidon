# Instalação do Harness Poseidon

## Escolha do modo

- **Pessoal**: Launcher + Host embarcado, SQLite e navegador local. Não requer IDE, Node nem .NET
  instalado na máquina de destino.
- **Servidor**: Host self-contained, PostgreSQL, até 30 usuários, OIDC opcional e execução atrás de
  proxy/reverse proxy administrado. O banco e o IdP são dependências operacionais externas.

Use somente artefatos produzidos de um commit validado por `tools/backend/verify.sh`.

## Release Candidate pessoal

O mantenedor gera a candidata, a partir de `develop` limpo, com:

```bash
./poseidon release-candidate
```

O comando para no primeiro warning, segredo, drift, recurso Docker órfão ou alteração versionada e
grava pacote, checksums, manifesto, SBOM e relatórios em
`.artifacts/release-candidate/<sha>-osx-arm64/`. Envie ao usuário o `.tar.gz` e os arquivos irmãos
`SHA256SUMS`, `INSTALLATION.md`, `HOMOLOGATION.md`, `RELEASE_NOTES.md` e `TEST_REPORT.md`.

No Mac de destino, sem IDE, Node ou SDK .NET:

```bash
shasum -a 256 -c SHA256SUMS
tar -xzf poseidon-<sha>-osx-arm64.tar.gz
cd poseidon-<sha>-osx-arm64
./Harness.Launcher install --install-dir "$PWD/../Poseidon"
cd ../Poseidon
./poseidon doctor
./poseidon start
```

O navegador abre automaticamente e a URL também é impressa. Para uma base descartável de
homologação, use `./poseidon start --demo`; a carga demo é opcional, idempotente, não contém segredo
e só ocorre em banco vazio. Pare com `./poseidon stop`. Dados ficam em `~/.harness-poseidon` e logs
em `~/.harness-poseidon/logs` por padrão; defina `POSEIDON_DATA_DIR` para isolar outra instalação.

## Publicação manual do pacote pessoal

No checkout de release:

```bash
tools/backend/publish-desktop.sh osx-arm64
```

Use o comando `Harness.Launcher install --install-dir <destino> [--data-dir <caminho>]` dentro de
`.artifacts/desktop/osx-arm64/`; ele valida o manifesto SHA-256 antes da cópia e registra a
instalação gerenciada. Depois execute o binário instalado com `--no-browser` para smoke sem
interface ou sem a opção para abrir o navegador. O data dir padrão é `~/.harness-poseidon`; o
pacote nunca pode ser instalado dentro do data dir. O worker self-contained fica em
`runner/Harness.Runner` (ou `.exe` no RID Windows) e também é coberto pelo manifesto.

O diretório `docs/` do pacote contém as notas e os roteiros. O manifesto rejeita arquivo ausente,
extra, symlink, tamanho ou SHA-256 divergente antes de instalar.

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

O gate valida sintaxe/permissões dos publicadores, comando raiz, Launcher, Host PostgreSQL e
backup/restore reais. O agregador da RC acrescenta E2E/a11y/Storybook e o lifecycle do pacote. O
smoke Entra ID real continua separado porque depende da app registration.
