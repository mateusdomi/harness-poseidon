# Harness Desktop — instalação, operação e desinstalação (modo pessoal)

## Gerar o pacote

```bash
tools/backend/publish-desktop.sh osx-arm64   # também: osx-x64, win-x64, linux-x64
```

Saída em `.artifacts/desktop/<rid>/`: executável `Harness.Launcher` self-contained (não requer .NET nem Node instalados), Host embarcado e `wwwroot/` com o frontend buildado.

## Instalar

Copie a pasta publicada para o destino (ex.: `~/Applications/Harness/`). Nenhum outro passo é necessário.

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

Substitua a pasta do aplicativo pela nova versão publicada. O data dir é separado do binário; as migrations idempotentes atualizam o banco no primeiro start. Recomenda-se `POST /api/v1/backups` antes de atualizar.

## Desinstalar com segurança

1. Encerre o launcher (Ctrl+C ou kill do processo).
2. (Opcional) exporte um backup: `POST /api/v1/backups` e copie `<data-dir>/backups/`.
3. Apague a pasta do aplicativo.
4. Os dados permanecem em `~/.harness-poseidon/` até você removê-los explicitamente — a desinstalação nunca apaga dados do usuário.
