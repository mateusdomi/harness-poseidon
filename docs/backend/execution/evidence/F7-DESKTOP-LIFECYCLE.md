# Evidência F7-2 — ciclo de vida desktop seguro

Data: 2026-07-19.

## Entrega

O `Harness.Launcher` ganhou comandos fortemente tipados `package-manifest`, `install`, `update` e `uninstall`:

- o publish desktop contém Launcher/Host, Runner self-contained em `runner/`, SPA e manifesto SHA-256 fechado de todos os arquivos;
- install verifica tamanho/hash, recusa arquivo extra, traversal, symlink, pacote incompleto e diretório existente sem recibo gerenciado;
- update exige o mesmo data dir do recibo, recusa Launcher ativo por lease exclusivo e cria backup offline consistente de SQLite + catálogo no formato restaurável existente;
- a nova versão é copiada para staging e trocada por rename; falha no swap restaura a pasta anterior;
- pacote já instalado é no-op sem novo backup;
- uninstall exige recibo gerenciado, remove somente a pasta da aplicação e preserva data dir, banco, catálogo, backups e lease; repetição é no-op;
- raiz do volume, home, data dir sobreposto e symlink nunca são alvos válidos.

`LauncherProcessLease` impede dois Launchers no mesmo data dir e fecha a corrida entre execução e manutenção. O arquivo PID pode permanecer após crash para diagnóstico, mas a exclusividade é determinada pelo handle aberto, não por PID reutilizado.

## Provas

`DesktopLifecycleTests` executa install v1, repetição idempotente, pacote v2 corrompido sem alteração do destino, bloqueio com Launcher ativo, update v2, leitura do backup SQLite (valor 42), catálogo preservado, ausência do arquivo obsoleto, repetição sem segundo backup, uninstall, dados preservados e recusa da raiz. `LauncherSmokeTests` também comprova o lease e a recusa do segundo Launcher.

O publish `osx-arm64` real gerou manifesto com mais de 500 arquivos e componentes obrigatórios. Fora do source package, o artefato foi instalado, comprovou executáveis Launcher/Runner, passou `codesign --verify` (assinatura ad-hoc do apphost), iniciou sem IDE/SDK, respondeu `/health`, encerrou por SIGTERM e foi desinstalado com `harness.db` e lease preservados. Os diretórios ignorados `.artifacts/desktop-lifecycle-smoke.6PUZAO` e `.artifacts/desktop-runner-smoke.wc6CeT` retêm a evidência não versionada.

Gate integral esperado desta fatia: 245/245 backend e 331/331 frontend. A distribuição com Developer ID/notarização e o aceite em macOS limpo pertencem ao GNG-4 externo; não há credencial Apple configurada no escopo.
