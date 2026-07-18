# Evidência F1 — correção imutável e reidratação da cadeia

- Executado em: 2026-07-18T15:30:25Z
- Incremento: EP-09b.2b.2
- Resultado: verde

`IWorkChainStore` recebeu o comando de correção `WorkInstructionVersionCreateCommand` e o snapshot hierárquico completo `WorkChainAggregateSnapshot`. A correção exige hash SHA-256 válido, versão otimista atual, tarefa em `ready` e última tentativa rejeitada. A instrução anterior permanece imutável, a nova versão aponta `supersedesId` para ela e estado, Inbox, ledger e Outbox são confirmados atomicamente.

A reidratação retorna Solicitação, todas as Demandas e Tarefas, cada versão de instrução, cada tentativa, evidências ordenadas e review. SQLite executa a leitura sob transação dentro do dispatcher único. PostgreSQL usa uma transação `REPEATABLE READ`, impedindo que as consultas hierárquicas observem revisões diferentes do estado.

O mesmo cenário executado nos dois providers comprovou:

- o critic rejeitou a tentativa 1 e a tarefa retornou a `ready` na versão 4;
- reutilizar a instrução rejeitada foi recusado sem produzir transição;
- a correção criou instrução v2, preservou v1 e registrou `v2.supersedesId = v1.id`;
- replay da correção preservou o ledger original;
- tentativa 2 usou exclusivamente a instrução v2, concluiu com nova evidência e foi aprovada por critic independente;
- snapshot resumido final: tarefa `completed`, versão 8, instrução v2, 2 tentativas, 2 evidências e 2 reviews;
- snapshot agregado completo: versões `[1,2]`, tentativas `[1,2]`, estados `[rejected,approved]`, evidência e review correspondentes a cada tentativa;
- leitura de Solicitação inexistente retornou ausência sem efeito colateral.

Na primeira execução PostgreSQL, somente o caminho de ausência falhou porque tentava confirmar a transação com o reader do cabeçalho aberto. O reader passou a ser fechado explicitamente antes do commit em ambos os providers; a repetição focada PostgreSQL passou.

Evidência executada: SQLite focado 1/1, PostgreSQL focado 1/1 após a correção; `tools/backend/verify.sh` exit code 0; restore locked e format verdes; build Release 0 warnings/0 errors; suíte 74/74 (`Unit 46`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 12`). Cleanup final: zero containers, volumes ou networks com `com.harness.managed=true`.

Com esta evidência, EP-09b.2b está concluído. A próxima fatia da Fase 1 é EP-10, iniciando pelo contrato provider-neutral de WorkflowDefinition versionada, WorkflowRun, fases, gates e progresso objetivo recomputável.
