# Evidência F11-3 — scanning de segredos (gate contínuo + entrypoint operacional)

Data: 2026-07-19.

Fecha o item "secret scanning" do DoD da F11, complementando a auditoria de dependências já
ativa (`NuGetAudit=all` no restore).

- **Gate contínuo** (`tests/Harness.ArchitectureTests/SecretHygieneTests.cs`): a cada execução da
  suíte, enumera os arquivos **rastreados pelo Git** (`git ls-files -z`, pulando lock files, SBOM
  e binários) e falha se algum contiver um segredo de **alta precisão**: token de bot Telegram
  (`\d{6,10}:AA…`), bloco de chave privada PEM, AWS access key id (`AKIA…`), token Slack
  (`xox[baprs]-…`) ou Google API key (`AIza…`). Padrões escolhidos para **zero falso-positivo**
  no repositório atual. 1/1 verde; suíte de arquitetura passa de 6→7.
- **Entrypoint operacional** (`tools/backend/scan-secrets.sh`): espelha os mesmos padrões via
  `git grep -nE -e`, exit != 0 se encontrar. Executado nesta sessão: **exit 0, nenhum segredo**.

Motivação imediata: o token do bot `SystemPoseidon_bot` foi fornecido para o smoke real do
Telegram. O token é usado **exclusivamente** como variável de ambiente do processo Host
(`Harness__Channels__Telegram__BotToken`) e **nunca** é escrito em Git, banco, docs ou logs — este
gate garante que uma regressão que o commitasse por engano quebraria a suíte.

Gate: format sem mudanças; build Release 0 warnings/0 erros; suíte de arquitetura 7/7; suíte
integral **231/231**. Frontend preservado.
