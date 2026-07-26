# Coordenação

## Unidade de trabalho

Toda execução especializada parte de um card persistido. O card define objetivo,
escopo, exclusões, entradas, saídas, critérios de aceite, definição de pronto,
dependências, riscos, evidências obrigatórias, ferramentas, orçamento e
proveniência. Trabalho sem card é rejeitado.

Bruna cria e prioriza cards. O orquestrador atribui a execução somente depois de
validar prontidão, dependências, capacidade, autorização e isolamento. O agente
recebe um handoff mínimo e retorna resultado estruturado; histórico interno da
Bruna e contexto não selecionado não são delegados.

## Execução e concorrência

- Cada tentativa possui lease, heartbeat e fencing token.
- Lease expirada invalida resultados tardios e reenfileira o card de forma
  idempotente.
- Cada agente escreve somente nos paths cobertos por seu ScopeClaim.
- Cards paralelos declaram `provides` e `consumes`; fan-in aguarda a barreira de
  dependências.
- Integrações em `develop` são serializadas pelo coordenador de merge.
- Bloqueios registram causa, dependência, evidência e condição objetiva de saída.
- Retry preserva contexto, checkpoints e idempotency key; não duplica efeitos.

## Handoff e conclusão

O handoff contém objetivo, escopo, exclusões, entradas, critérios de aceite,
restrições, ferramentas autorizadas, referências de memória e proveniência. O
resultado contém estado, evidências, decisões, riscos, bloqueios, artefatos, custo,
tokens e proveniência.

Código só avança após revisão por agente distinto. Gates sem evidência válida
falham. Transições são aplicadas com controle de concorrência e registradas no
ledger append-only.
