# Revisão de código

## Independência

Todo card que altera código, testes, migrations, ferramentas, infraestrutura ou
configuração executável exige revisão por agente distinto. `Actor` nunca pode ser
igual a `Approver`; identidade ausente ou ambígua mantém o gate fechado.

O pedido de revisão referencia card, diff, testes, critérios de aceite e revisor. O
veredito registra aprovação ou reprovação, achados P0 a P3, evidências, correções
obrigatórias e número do ciclo.

## Critérios

O revisor verifica:

- aderência ao objetivo, escopo e contratos públicos;
- correção, idempotência, concorrência, recuperação e tratamento de falhas;
- isolamento de tenant, autorização, segredos e conteúdo não confiável;
- compatibilidade entre SQLite e PostgreSQL quando houver persistência;
- testes de regressão e gates relevantes;
- ausência de mudanças alheias ou arquivos fora do catálogo;
- clareza suficiente para operação e rollback.

## Ciclos e merge

Reprovação retorna o card para execução com correções verificáveis. Ciclos têm
limite; excedê-lo escala o card para replanejamento pela Bruna. Somente submissão
aprovada, com fencing token vigente e gates verdes, pode entrar na fila serializada
de merge. A aprovação e o merge são registrados no ledger.
