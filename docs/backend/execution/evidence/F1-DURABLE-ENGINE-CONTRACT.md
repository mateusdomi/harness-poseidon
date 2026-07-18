# Evidência F1 — contrato do motor durável

- Executado em: 2026-07-18T14:05:56Z
- Incremento: EP-05a
- Resultado: verde

Foi materializada a primeira versão completa de `IDurableExecutionEngine`, incluindo iniciar, pausar, retomar, cancelar, adquirir/renovar lease, heartbeat, checkpoint, concluir, falhar com retry, sinalizar evento, agendar/disparar timer, reconciliar e consultar estado. Lease e comandos carregam `attemptId`, owner e fencing token; checkpoints carregam chave e idempotency key; todas as operações preservam tenant explícito.

A máquina de estados é determinística. Um teste percorreu todos os pares dos oito estados e comparou o resultado com a matriz declarada; estados `Completed`, `Cancelled` e `DeadLetter` não possuem transição de saída. O retry exponencial usa multiplicador decimal, valida limites e é capado sem overflow pelo delay máximo. Para `2 s × 2,5`, o teste comprovou `2 s`, `5 s`, `12,5 s`, `20 s` e permanência em `20 s` até a falha 200.

Gate: `tools/backend/verify.sh` exit 0; restore locked e format verdes; build Release 0 warnings/0 errors; 58/58 testes verdes (`Unit 34`, `Architecture 6`, `Concurrency 3`, `Recovery 2`, `Contract 3`, `Integration 10`). Nenhuma migration ou implementação de provider é declarada concluída nesta evidência; esse é o incremento EP-05b seguinte.
