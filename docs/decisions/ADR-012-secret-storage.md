# ADR-012 — Segredos no macOS Keychain e proxy de credenciais

- Status: aceito
- Data: 2026-07-18

## Decisão

`ISecretStore` usa macOS Keychain no primeiro ciclo. Agentes não recebem segredos no ambiente; acesso externo passa por proxy de credenciais autorizado.

## Consequências

Segredo nunca aparece em texto puro, log, transcript, `.env` commitado ou resposta de API. DPAPI e stores corporativos permanecem futuros.
