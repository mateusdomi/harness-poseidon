# Evidência F7-1 — Launcher real e publish desktop self-contained

Data: 2026-07-19.

O `Harness.Launcher` deixou de ser stub: `LauncherApplication` sobe o Host in-process com porta dinâmica em loopback (ou fixa via `--port`), resolve o endereço real pelo `IServerAddressesFeature`, cria o diretório de dados do usuário (padrão `~/.harness-poseidon`, customizável via `--data-dir`) apontando o SQLite para lá, abre o navegador da plataforma (`open`/`start`/`xdg-open`, suprimível com `--no-browser`) e encerra graciosamente. Argumentos inválidos falham fechado com mensagem clara.

`tools/backend/publish-desktop.sh <rid>` produz o pacote **self-contained** (sem .NET nem Node na máquina de destino): builda o frontend, publica o Launcher com o Host e copia o `wwwroot` embarcado ao lado do executável — exatamente o candidato que `ResolveFrontendPath` resolve em produção.

Provas executadas:

- **Smoke in-process** (`LauncherSmokeTests`): porta dinâmica real atribuída, `harness.db` criado no data dir informado, `/health` respondendo `healthy`, SPA servida com `<div id="root">` e parse de argumentos inválidos rejeitado. Suíte integral 211/211.
- **Smoke do binário publicado** (osx-arm64, 129 MB, sem IDE): `./Harness.Launcher --no-browser --port 5091 --data-dir <tmp>` aplicou as 30 migrations no data dir, respondeu `{"status":"healthy"}`, serviu o `index.html` do frontend embarcado, preservou 404 de API e encerrou deixando somente `harness.db` no data dir.

`docs/backend/execution/DESKTOP.md` documenta gerar/instalar/executar/backup/atualizar/desinstalar com a garantia de que a desinstalação nunca apaga dados do usuário. Duas falhas transitórias de corrida entre os testes PostgreSQL de assemblies distintos foram observadas e passaram isoladas e na reexecução integral (flakiness pré-existente de containers concorrentes, anotada para hardening F11).

Pendências F7 conscientes: empacotamento instalável (dmg/zip assinado), verificação de update automática e Runner ao lado do Host no pacote — o Runner permanece cliente IPC de teste até o executor remoto precisar dele. Gate: format sem mudanças; build Release zero warnings/erros; 211/211 (`Unit 116`, `Integration 53`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`).
