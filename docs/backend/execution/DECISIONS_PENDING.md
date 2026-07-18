# Decisões pendentes e suposições

Decisões não bloqueadoras são tomadas pela opção mais segura e promovidas a ADR quando arquiteturais.

| ID | Tema | Estado/decisão provisória | Próxima ação |
|---|---|---|---|
| DP-001 | arquivo físico do prompt v1.3 ausente | usar integralmente o conteúdo fornecido na conversa como fonte de verdade | catalogar sem criar dependência externa |
| DP-002 | tooling local ignorado sem editar `.gitignore` compartilhado | usar exclusão local em `.git/info/exclude` | adiar alteração de `.gitignore` até coordenação com Kimi |
| DP-003 | contrato frontend usa 25 eventos e nomes diferentes do catálogo v1.3 | contrato v1.3 prevalece no backend; manter aliases/compatibilidade somente se justificados | detalhar em `docs/contracts/CONTRACT_RECONCILIATION.md` e testes de drift |
| DP-004 | ArchUnitNET versus verificador próprio | pesquisar compatibilidade com .NET 10 e registrar ADR | resolver no bootstrap de testes de arquitetura |

Nenhuma decisão jurídica, compra, credencial ou publicação em `main` está autorizada.
