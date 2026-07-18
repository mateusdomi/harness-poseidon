# Progresso e evidências

## Estado dos gates

| Gate | Critério resumido | Estado | Evidência |
|---|---|---|---|
| GNG-1 | nove PoCs verdes | fechado | nenhuma PoC executada ainda |
| GNG-2 | recuperação abrupta com auditoria completa | fechado | F1 não iniciada |
| GNG-3 | dogfood integrado com validação humana | fechado | F2 não iniciada |
| GNG-4 | instalação limpa e licença offline | fechado | F7/F8 não iniciadas |
| GNG-5 | carga, isolamento e failover | fechado | F10 não iniciada |
| GNG-6 | hardening e DoD global | fechado | F11 não iniciada |

## Fase 0

| Incremento | Estado | Última evidência |
|---|---|---|
| Inventário de ambiente/Docker | executado | comandos concluídos com exit code 0 em 2026-07-18; 11 containers parados, 14 volumes, 7 networks, zero recursos Harness |
| SDK .NET local | executado e validado | SDK 10.0.302 e runtimes 10.0.10; `dotnet --info` exit code 0 em 2026-07-18 |
| Bootstrap/pipeline | pendente | — |
| PoC-1 SQLite/dispatcher | pendente | — |
| PoC-2 retomada | pendente | — |
| PoC-3 lease/fencing | pendente | — |
| PoC-4 Codex CLI | pendente | — |
| PoC-5 Git fixtures/claims | pendente | — |
| PoC-6 sandbox | pendente | — |
| PoC-7 SignalR | pendente | — |
| PoC-8 PostgreSQL | pendente | — |
| PoC-9 IPC | pendente | — |

Conclusão só será registrada após execução. Arquivo existente ou teste apenas escrito não conta como evidência.
