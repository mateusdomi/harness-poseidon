# Catálogo de módulos

| Módulo | Responsabilidade principal |
|---|---|
| Identity | perfil local, sessão e futura identidade federada |
| Organizations | tenant, identidade visual e políticas herdáveis |
| Projects | projetos, repositórios e contexto de operação |
| Conversations | conversas, mensagens e turnos streaming |
| Coordination | chefe, mailbox, digest e cadeia de negócio Solicitação→Demanda→Tarefa→Instrução→Tentativa→Revisão |
| Workflows | definições, versões, runs, fases, gates e progresso |
| Execution | execução técnica das tentativas, leases, checkpoints, sandbox e motor durável |
| Agents | definições, versões, instâncias e executores |
| Providers | contas, modelos, routing, budgets e quotas |
| Documents | documentos, versões, aprovações, catálogo e busca |
| Governance | ledger, auditoria, approvals, decisões e políticas |
| Notifications | notificações, dedupe e entrega |
| Prototyping | protótipos, referências, galerias e waivers |
| Tools | ferramentas, skills, plugins e MCP |
| Licensing | licenças assinadas e entitlements sem bloquear leitura de dados |

Cada módulo contém `Domain/`, `Application/`, `Infrastructure/` e `Contracts/`. Referência direta permitida: SharedKernel e contratos explicitamente autorizados de outros módulos.
