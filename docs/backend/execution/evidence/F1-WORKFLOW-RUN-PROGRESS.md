# Evidência F1 — objetivos, gates, fases e progresso de WorkflowRun

- Executado em: 2026-07-18T16:21:54Z
- Incremento: EP-10b.2b.3
- Resultado: verde

`IWorkflowStore` agora expõe comandos tipados para avançar objetivo, avaliar gate e concluir fase, todos com versão esperada e Inbox idempotente. O contrato também publica um snapshot hierárquico contendo run, fases, objetivos, gates, requisitos, versões, timestamps e progresso executado/validado/aprovado.

As regras persistidas correspondem ao agregado de domínio:

- objetivo comum avança somente `pending→executed→validated→approved`;
- objetivo de kind `gate` recusa avanço comum e só é aprovado por avaliação positiva;
- gate recusa avaliação enquanto qualquer requisito estiver abaixo do mínimo;
- gate `failed` pode ser reavaliado, mas `passed` não retrocede;
- fase exige todos os gates passed e nenhum objetivo pending;
- conclusão ativa exatamente a próxima fase pendente ou encerra o run;
- cada aplicação incrementa a versão global do run e a versão da projeção alterada;
- rejeições esperadas são persistidas somente na Inbox; ledger e Outbox existem apenas para aplicações.

SQLite mantém a transação sob o dispatcher único. PostgreSQL usa advisory locks por chave/run, linha do run sob `FOR UPDATE` e ledger serializado por tenant. A reidratação SQLite ocorre no dispatcher; a PostgreSQL usa transação `REPEATABLE READ` para não combinar estados de instantes diferentes.

O comportamento comum percorreu duas fases com pesos `2+1+1`:

- gate prematuro, avanço de objetivo-gate e salto de estado foram recusados sem alterar a versão;
- 10 avanços concorrentes idênticos resultaram em 1 aplicação e 9 replays;
- progresso intermediário após o primeiro objetivo executado foi `50/0/0`;
- gate foi registrado como failed, conclusão ficou bloqueada, requisito avançou a approved e o retry passou;
- fase analysis concluiu e delivery tornou-se a única fase ativa;
- objetivo da fase antiga foi recusado; delivery avançou os três estados;
- a última conclusão fechou o run em v14 com ambas as fases completed e progresso recomputado `100/100/100`;
- snapshot recompôs 2 fases, 3 objetivos, 1 gate e requisito `requirements`; run inexistente retornou ausência;
- tentativa de alterar objetivo após conclusão retornou `InvalidState`.

A primeira compilação detectou import ausente para `AuditLedgerHash`; a referência ao namespace comum foi adicionada e a repetição SQLite passou 1/1. O mesmo comportamento PostgreSQL passou 1/1 após inventário Docker.

Gate integral: `tools/backend/verify.sh` exit code 0; restore locked/format verdes; build Release 0 warnings/0 errors; 84/84 testes (`Unit 55`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 13`). Cleanup final: zero containers, volumes ou networks com `com.harness.managed=true`.

EP-10 está verde no escopo da fundação. O próximo incremento inicia documentos versionados, aprovações e detecção de órfãos.
