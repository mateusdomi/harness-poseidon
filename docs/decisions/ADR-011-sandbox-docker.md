# ADR-011 — Sandbox Docker no macOS

- Status: aceito
- Data: 2026-07-18

## Decisão

`ISandboxProvider` começa com Docker Desktop detectado no macOS. Cada tentativa recebe worktree isolada, limites de CPU/memória/disco/tempo, rede por proxy obrigatório e cleanup. Recursos usam prefixo `harness-` e label `com.harness.managed=true`.

## Consequências

Home não é montada; cleanup nunca toca recursos sem label. Fallback sem sandbox exige aceite explícito, auditoria e exposição pela API.
