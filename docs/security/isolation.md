# Isolamento

## Dimensões

Isolamento é aplicado por tenant, projeto, card, tentativa, agente, filesystem,
ferramenta, rede e store. Identificadores são validados em cada fronteira e nunca
inferidos de conteúdo não confiável.

## Execução

Cada tentativa recebe:

- worktree efêmera e ScopeClaim limitado;
- sandbox sem credenciais herdadas por padrão;
- capability token com ator, recursos, ferramentas, prazo e fencing;
- egress por allowlist;
- diretórios de entrada e saída explícitos;
- limites de CPU, memória, tempo e tamanho de artefato.

O host não aceita resultado de lease expirada. Cleanup só remove paths resolvidos e
pertencentes à tentativa. Checkpoints preservam recuperação sem abrir acesso a
outros cards.

## Dados

No modo servidor, isolamento de tenant é reforçado no banco e no artifact store. No
modo pessoal, permissões de filesystem e escopo lógico mantêm a mesma separação.
Índices vetoriais são derivados, incluem tenant e projeto e podem ser reconstruídos.

Falha em comprovar a fronteira mantém a operação bloqueada.
