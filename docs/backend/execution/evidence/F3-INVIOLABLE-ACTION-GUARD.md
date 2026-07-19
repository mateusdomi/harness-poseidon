# Evidência F3-2 — bloqueios invioláveis do modo autônomo

Data: 2026-07-19.

`AutonomousActionGuard` (módulo Governance) modela as sete ações proibidas da missão como conjunto fechado (`GuardedActionKind`: deploy em produção, exclusão de dados, alteração de política, push em branch protegida, gasto acima do budget, alteração de regra de negócio sem requisito aprovado, uso de credencial não autorizada). A avaliação é inviolável por construção: qualquer ator automatizado (Chief, agente ou sistema) é negado em **todos** os modos de operação — inclusive `autonomous` — e uma referência de aprovação forjada por um agente não altera a decisão; somente um humano com aprovação explícita registrada atravessa o guard; humano sem aprovação também é negado; modo fora do conjunto fechado lança.

Tentativas bloqueadas são auditáveis: `IAuditEventStore` ganhou `AppendAsync` tipado que grava no ledger encadeado (`audit.eventAppended` com envelope `auditEvent` que a projeção de governança já lê) e publica no Outbox na mesma transação.

Imposição real no fluxo de execução: o endpoint de execução isolada consulta os budgets do tenant antes de orquestrar; um budget global ou do projeto com `spent >= limit` aciona o guard (`budget_overrun`), grava `governance.actionBlocked` com o attempt como alvo e devolve 409 — nenhum claim, worktree ou sandbox é criado. As demais ações guardadas permanecem estruturalmente impossíveis pela plataforma nos pontos existentes (branch de tentativa exige prefixo `task/` por CHECK no schema e pelo `GitWorktreeManager`; push/merge externos não existem como capacidade; segredos nunca entram no ambiente do agente), e o guard central é o contrato para qualquer capacidade futura.

Testes: `AutonomousActionGuardTests` percorre a matriz exaustiva 7 ações × 3 modos × 3 atores automatizados (todas negadas), a burla com aprovação forjada (negada), humano sem/na aprovação e modo inválido. O teste de integração comprova o bloqueio por budget de ponta a ponta via API — 409, evento de auditoria com `budget_overrun` e alvo correto — e que o mesmo attempt executa normalmente após o budget voltar ao limite.

Gate: format sem mudanças; build Release zero warnings/erros; backend 190/190 (`Unit 101`, `Integration 47`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`). Próximo: F3-3 — verificação de consistência em camadas.
