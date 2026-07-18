# ADR-011 — Sandbox Docker no macOS

- Status: aceito
- Data: 2026-07-18

## Decisão

`ISandboxProvider` começa com Docker Desktop detectado no macOS. Cada tentativa recebe worktree isolada, limites de CPU/memória/disco/tempo, rede por proxy obrigatório e cleanup. Recursos usam prefixo `harness-` e label `com.harness.managed=true`.

O container do agente participa somente de uma bridge `--internal`. O proxy participa dessa bridge e de uma segunda rede de egress; targets externos nunca participam da rede do agente. `HTTP_PROXY` e `HTTPS_PROXY` apontam para o proxy, que aplica allowlist e nega outros destinos. Containers executam como usuário não-root, com rootfs read-only, `no-new-privileges`, capabilities removidas, limites de CPU/memória/PIDs, writable layer limitada por storage option e `/tmp` em tmpfs limitada. A home do host não é montada.

Cleanup descobre recursos pela label da tentativa, exige simultaneamente prefixo `harness-` e `com.harness.managed=true` verificado via `inspect`, e só então remove alvos exatos. Containers são removidos antes de networks/volumes/imagem. Prune permanece proibido.

## Consequências

O bind mount da worktree é gravável e não recebe quota nativa do Docker Desktop; storage layer e scratch estão limitados, mas a Fase 1 adicionará medição/watchdog do tamanho da worktree e pausa por budget de disco. Isolamento de rede reduz egress convencional, mas não substitui hardening do daemon/VM do Docker. Fallback sem sandbox exige aceite explícito, auditoria e exposição pela API.
