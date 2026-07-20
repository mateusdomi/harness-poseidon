# Regra canônica — Git e preservação de trabalho

Owner: Platform Engineering. Versão: 1.0.0.

- O repositório Poseidon possui somente `main` e `develop`; trabalho diário usa
  `develop`. Force push e merge em `main` são proibidos sem autorização humana.
- Antes de editar, inspecione branch e working tree. Mudanças existentes são do
  usuário até prova em contrário; preserve-as e não use reset/checkout destrutivo.
- Antes de push: gate aplicável verde, commit coerente, `git fetch origin`, rebase
  não destrutivo sobre `origin/develop`, repetição dos testes afetados e push
  normal. Conflito nunca é resolvido apagando trabalho válido.
- Branches/worktrees por tentativa pertencem apenas a repositórios administrados
  pelo produto, sob raiz controlada, claims persistidos e cleanup idempotente.
- Commits são intencionais e rastreáveis; evidência registra SHA e comandos, sem
  segredos. Hooks locais podem antecipar falhas, mas CI/runtime são autoridade.

Enforcement: branch/release gates, policy de claims e testes Git/recovery.
