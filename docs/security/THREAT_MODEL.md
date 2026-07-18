# Threat model inicial

## Ativos

Estado de projetos/tenants, repositórios e artefatos, credenciais de providers, ledger, licenças, budgets e comandos executados por agentes.

## Fronteiras de confiança

- Browser/canal → Host.
- Runner/sandbox → IPC loopback do Host.
- Host → SQLite/PostgreSQL.
- Host/Runner → Docker e repositórios administrados.
- Host → Keychain/proxy de credenciais/providers externos.

## Ameaças e controles iniciais

| Ameaça | Controle obrigatório |
|---|---|
| Runner forja/reenvia conclusão | token efêmero, sequência, Inbox/idempotência, fencing |
| agente executa comando não autorizado | ferramenta tipada, allowlist e policy check por risco |
| escape/exfiltração de sandbox | limites, worktree isolada, sem home/segredo, proxy default-deny |
| confusão entre tenants/projetos | IDs tipados, filtros obrigatórios, RBAC/ABAC e testes adversariais |
| alteração/remoção de auditoria | ledger append-only com hash encadeado |
| segredo em log/API | redaction estrutural e referências opacas de segredo |
| replay de webhook/evento | Inbox com chave idempotente e dedupe por canal |
| owner vencido grava estado | lease + fencing token transacional |
| recurso Docker de terceiro removido | prefixo e label obrigatórios; cleanup filtrado |
| licença expirada sequestra dados | leitura/exportação sempre disponíveis |

Este modelo será aprofundado formalmente na Fase 11 e revisado sempre que uma PoC revelar limitação arquitetural.
