# Harness Desktop — instalação, operação e desinstalação (modo pessoal)

## Gerar o pacote

```bash
tools/backend/publish-desktop.sh osx-arm64   # também: osx-x64, win-x64, linux-x64
```

Saída em `.artifacts/desktop/<rid>/`: executáveis self-contained do Launcher/Host e Runner (não requer .NET nem Node instalados), `wwwroot/` com o frontend buildado e manifesto SHA-256 de todos os arquivos. A instalação recusa pacote que não contenha os quatro componentes obrigatórios: Launcher, Host, Runner e SPA.

## Instalar

Execute a partir da pasta publicada:

```bash
./Harness.Launcher install \
  --install-dir "$HOME/Applications/Harness" \
  --data-dir "$HOME/.harness-poseidon"
```

O instalador valida integralmente o manifesto, recusa symlinks/arquivos extras, não sobrescreve pasta não gerenciada e grava um recibo que vincula a instalação ao data dir. Repetir o mesmo comando com o mesmo pacote é um no-op verificável.

## Executar

```bash
./Harness.Launcher                # porta dinâmica, abre o navegador
./Harness.Launcher --port 5090    # porta fixa
./Harness.Launcher --no-browser   # sem abrir navegador
./Harness.Launcher --data-dir ~/dados-harness   # data dir customizado
```

O launcher imprime a URL local e o diretório de dados. Todos os dados (SQLite `harness.db`, catálogo de documentos, anexos e backups) ficam no data dir — padrão `~/.harness-poseidon/`. Logs vão para stdout/stderr; redirecione para arquivo se desejar (`./Harness.Launcher > harness.log 2>&1`).

## Backup, restore e diagnóstico

Pela API autenticada por sessão local: `POST /api/v1/backups`, `POST /api/v1/backups/{id}/restore`, `GET /api/v1/diagnostics`. Os backups ficam em `<data-dir>/backups/<ulid>/` com manifesto.

## Atualizar

Encerre o Launcher e, a partir da **nova** pasta publicada, execute:

```bash
./Harness.Launcher update \
  --install-dir "$HOME/Applications/Harness" \
  --data-dir "$HOME/.harness-poseidon"
```

Antes da troca, o comando cria um backup SQLite consistente + catálogo em `<data-dir>/backups/<ulid>/`. O novo pacote é validado e copiado para staging; a pasta anterior só é substituída por rename e é restaurada automaticamente se a troca falhar. Pacote corrompido, Launcher ainda ativo, data dir divergente, destino inseguro ou instalação não gerenciada falham sem tocar na versão corrente. As migrations idempotentes atualizam o banco no primeiro start.

## Desinstalar com segurança

1. Encerre o launcher (Ctrl+C).
2. A partir de qualquer pacote publicado íntegro, execute:

   ```bash
   ./Harness.Launcher uninstall \
     --install-dir "$HOME/Applications/Harness" \
     --data-dir "$HOME/.harness-poseidon"
   ```

3. O comando exige o recibo gerenciado, valida o vínculo com o data dir e remove apenas a pasta da aplicação. O data dir, banco, catálogo e backups permanecem intactos. Repetir a desinstalação é um no-op.

Instalação e data dir não podem se sobrepor; raiz do volume, home do usuário, symlinks e diretórios sem recibo nunca são alvos válidos de remoção.
