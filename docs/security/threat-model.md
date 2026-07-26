# Threat model

## Ativos e fronteiras

Ativos principais: autoridade do usuário e da Bruna, estado de cards e execuções,
segredos, código, artefatos, memória, contexto, ledger, canais e telemetria.
Fronteiras: usuário e canais, Control e Execution Plane, sandbox e host, tenants,
providers, MCP, bancos e artifact store.

## Ameaças prioritárias

- prompt injection direta e indireta;
- vazamento de system prompt, contexto, PII e segredos;
- tool ou MCP poisoning e confused deputy;
- capability forjada, expirada ou reutilizada;
- cruzamento de tenant, projeto, card ou worktree;
- path traversal, SSRF, zip bomb e execução de arquivo malicioso;
- exfiltração por rede, logs, traces, artefatos ou saída ao usuário;
- resultado tardio aceito após troca do fencing token;
- alteração de guardrails, canon, testes ou evidência;
- supply chain comprometida e dependência não bloqueada;
- agente executor publicando como Bruna ou aprovando a si próprio.

## Controles

PEP e capability tokens aplicam menor privilégio fora do modelo. Worktrees,
sandboxes e egress allowlist isolam tentativas. Fencing, OCC, outbox e ledger
protegem estado. Output Gateway limita publicação. Redação precede contexto e
telemetria. Scan de segredos, lockfiles, SBOM, SAST e revisão distinta protegem a
cadeia de entrega.

Ações de alto impacto, UAT, release e risco residual exigem humano. Nenhum controle
isolado é considerado suficiente contra prompt injection.
