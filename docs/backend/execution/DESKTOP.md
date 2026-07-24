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
./Harness.Launcher                # porta FIXA persistida, abre o navegador
./Harness.Launcher --port 5090    # porta fixa explícita (one-off, não altera a preferência)
./Harness.Launcher --no-browser   # sem abrir navegador
./Harness.Launcher --data-dir ~/dados-harness   # data dir customizado
```

O launcher imprime a URL local e o diretório de dados. Todos os dados (SQLite `harness.db`, catálogo de documentos, anexos e backups) ficam no data dir — padrão `~/.harness-poseidon/`. Logs vão para stdout/stderr; redirecione para arquivo se desejar (`./Harness.Launcher > harness.log 2>&1`).

### Porta fixa e persistente (PORTA-DINAMICA)

Sem `--port`, o launcher usa uma **porta fixa e persistente** para não quebrar o bookmark do
dono. No primeiro start ele adota a porta padrão `5173` (ou, se ocupada, uma porta livre próxima)
e a **salva** em `<data-dir>/port`. Nos starts seguintes reusa exatamente essa porta. Se a porta
preferida estiver ocupada num start específico, ele cai para uma porta livre vizinha **só naquele
start**, sem apagar a preferência — quando a porta preferida liberar, o endereço volta a ela
sozinho. Uma `--port` explícita vence tudo e nunca altera a preferência salva. `./poseidon status`
reporta a porta fixa em uso.

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

### Detecção automática de nova versão e auto-update (G-AUTOUPDATE)

A instalação carrega a versão corrente no recibo gerenciado (`.harness-desktop-install.json`, campo
`Version` = SHA do publish). Para detectar e aplicar uma versão nova **sem redeploy manual**, o
Launcher expõe dois verbos que **reusam a maquinaria de update** acima (verificação de integridade,
backup offline, rename atômico, rollback):

```bash
# Detecção read-only (pode rodar com o app no ar): compara a versão instalada com a do pacote-fonte.
./Harness.Launcher check-update --install-dir <install> --data-dir <data> [--source <dir-do-pacote-novo>]

# Gatilho: aplica a atualização a partir da fonte (encerre o Launcher antes).
./Harness.Launcher self-update  --install-dir <install> --data-dir <data> [--source <dir-do-pacote-novo>]
```

Pelo wrapper: `./poseidon check-update` e `./poseidon update` (o `update` encerra o app antes de
aplicar). `check-update` imprime JSON com `currentVersion`, `availableVersion`, `updateAvailable` e
o `packageDirectory` alvo; não toca no disco.

**Fonte da versão (sem servidor remoto por ora).** A “fonte de atualização” é o diretório de um
pacote publicado mais novo (produzido por `tools/backend/publish-desktop.sh`, com seu manifesto
`.harness-desktop-package.json` carregando a `Version`). Ela é informada por `--source <dir>` ou,
de forma persistente, pelo arquivo `<data-dir>/update-source.json`:

```json
{ "packageDirectory": "/caminho/para/o/pacote/publicado/mais/novo" }
```

O RID da fonte precisa bater com o da instalação; a detecção compara apenas as versões e é
idempotente (reaplicar quando já está na versão da fonte é no-op verificável).

**O que já está pronto:** detecção honesta de nova versão + gatilho de atualização reusando todo o
backup/rollback existente, por CLI (`check-update`/`self-update`) e pelo wrapper (`./poseidon
check-update`/`./poseidon update`).

**O que falta para ser 100% automático (sem servidor público de update ainda):**
1. **Servidor/canal remoto de versão + download**: hoje a fonte é um diretório local de pacote
   apontado por `update-source.json`. Um update totalmente automático exige uma origem remota
   (feed de versão assinado) que o app consulte periodicamente e da qual **baixe** o novo pacote
   para um staging local — hoje o pacote novo precisa já estar presente no disco.
2. **Checagem periódica em background + notificação na UI**: a detecção é sob demanda (verbo/wrapper).
   Falta um poller no Host e um aviso “nova versão disponível” na interface.
3. **Aplicação com reinício orquestrado**: `self-update` exige o Launcher encerrado e não reinicia o
   app sozinho; um fluxo 100% automático encerraria, aplicaria e reabriria sem intervenção.

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
