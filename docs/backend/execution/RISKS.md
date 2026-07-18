# Riscos ativos

| ID | Risco | Sinal/gatilho | Resposta | Estado |
|---|---|---|---|---|
| R-001 | contenção de escrita SQLite | `SQLITE_BUSY` não tratado na PoC-1 | dispatcher único; medir; ADR se exigir Postgres local opcional | monitorar |
| R-002 | Codex CLI instável para automação | falha de retomada/heartbeat na PoC-4 | checkpoints menores e reidratação por Git; medir antes de alternativa | monitorar |
| R-003 | egress Docker não isolável de modo forte | tráfego contorna proxy na PoC-6 | proxy obrigatório/default-deny; documentar limitação residual | monitorar |
| R-004 | drift com frontend em desenvolvimento | schemas/nome de evento divergem | OpenAPI/eventos canônicos + reconciliação explícita; nunca editar frontend | ativo |
| R-005 | SDK .NET 10 ausente globalmente | bootstrap `net10.0` não compila fora do wrapper | SDK 10.0.302 local e instalador idempotente | mitigado |
| R-006 | pushes concorrentes em `develop` | push rejeitado ou rebase conflita | fetch/rebase/teste novamente; preservar integralmente conteúdo Kimi | ativo |

Não há No-Go registrado. GNG-1 permanece fechado por trabalho ainda não executado, não por falha.
