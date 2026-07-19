# Evidência F2-DOGFOOD-1d.2c — recovery da composição por estágio

Data: 2026-07-19.

O `IsolatedAttemptOrchestrator` ganhou retomada determinística: ao readquirir uma tentativa não terminal cujo owner divergiu ou cuja lease expirou, ele recupera o claim via `ReclaimExpiredAsync` (fencing token elevado) e continua do estágio persistido; uma tentativa terminal com cleanup pendente executa somente a compensação (sandbox cleanup, remoção guard-railed da worktree e liberação do claim) sem reexecutar executor ou sandbox; uma lease ativa de outro owner devolve `Rejected` sem tocar em nada.

O teste `IsolatedAttemptRecoveryTests` simula morte abrupta em cada estágio da composição deixando exatamente o estado que um processo morto deixaria (registros duráveis com lease expirada + efeitos Git reais) e retoma com outro owner:

1. após o claim e antes da branch: retomada reclama a lease (fencing 1→2), cria branch/worktree, executa e conclui com cleanup completo;
2. após branch/worktree e antes do sandbox (`prepared` persistido): retomada não duplica branch nem worktree e preserva o commit SHA observado;
3. durante o executor (`running` persistido): retomada reexecuta o turno e persiste a sessão nova como metadado;
4. após a conclusão e antes do cleanup: retomada não reexecuta nada (zero aberturas de sandbox), apenas compensa cleanup e libera claims;
5. durante cleanup parcial (worktree já removida): a compensação tolera a ausência, não remove nada fora da raiz controlada e libera o claim;
6. lease ativa renovada pelo owner original: a retomada é rejeitada, o snapshot permanece com owner/fencing/estado intactos e nenhuma branch é criada.

Invariantes globais verificadas ao final: nenhuma branch duplicada, exatamente uma worktree registrada (a principal), todo sandbox aberto teve cleanup e todos os claims dos fluxos concluídos foram liberados.

Gate: `dotnet format` sem mudanças; build Release com zero warnings/erros; backend 179/179 (`Unit 96`, `Integration 41`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`). `frontend/**` e `docs/frontend/**` sem edição. Próximo passo: fiação DI + política do projeto externo, smoke classificado com Docker/Codex reais e o dogfood completo F2-DOGFOOD-2.
