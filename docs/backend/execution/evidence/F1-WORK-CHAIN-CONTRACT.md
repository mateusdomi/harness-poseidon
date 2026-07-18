# Evidência F1 — contrato da cadeia de trabalho

- Executado em: 2026-07-18T14:55:39Z
- Incremento: EP-09a
- Resultado: verde

`WorkChainAggregate`, no módulo Coordination, materializa a cadeia de negócio Solicitação → Demanda → Tarefa → Versão de instrução → Tentativa → Evidência → Revisão. O módulo Execution continua dono da execução técnica, leases, fencing e checkpoints; a tentativa de negócio referencia o trabalho produzido e aprovado.

Invariantes executadas:

- tenant, projeto e usuário entram como ULIDs canônicos e IDs internos são tipados;
- solicitação não possui operação de alteração;
- correções criam `InstructionVersion` append-only, com versão crescente, relação `SupersedesId` e SHA-256 calculado pelo domínio;
- apenas a versão mais recente pode iniciar uma tentativa e só uma tentativa fica ativa por tarefa;
- completion exige ao menos uma referência objetiva de evidência;
- risco médio, alto ou crítico impede o produtor de aprovar o próprio trabalho;
- risco baixo pode permitir self-review conforme política;
- rejeição mantém histórico e exige nova versão de instrução antes de outra tentativa;
- erros esperados retornam `Result` com códigos e message keys estáveis.

Evidência: teste focado `WorkChainAggregateTests` 8/8 verde; `tools/backend/verify.sh` exit code 0; build Release com 0 warnings/0 errors; suíte completa 73/73 (`Unit 46`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 11`).

Esta evidência não declara persistência concluída. Migrations e store dual-provider, incluindo Inbox, ledger e Outbox, são o próximo incremento EP-09b.
