# F2-WORK-1b — comandos e lifecycle do quadro

Data: 2026-07-18

## Escopo comprovado

- `POST /api/v1/solicitations/{id}/transitions` aplica a matriz fechada `open → inAnalysis → converted|answered|closed`, preserva conteúdo imutável e devolve conflito determinístico para regressões.
- `POST /api/v1/tasks/{id}/moves` publica as oito colunas do frontend, exige motivo no primeiro bloqueio e impede `done` antes da conclusão da autoridade de negócio.
- `POST /api/v1/tasks/{id}/priority` mantém prioridade do quadro e risk tier do actor–critic sincronizados.
- `POST /api/v1/tasks/{id}/instructions` e `POST /api/v1/task-instructions` criam versão imutável somente após tentativa rejeitada, com `supersedes`, autor humano e incremento otimista da tarefa.
- `IWorkChainStore` está registrado no Host. Start, completion e review projetam `development`, `review`, `corrections` e `done`, além de tentativas e `attempt-events` nos tipos canônicos `log`/`note`.
- Mudanças de quadro e instrução gravam ledger; eventos realtime aplicáveis gravam Outbox na mesma transação.

## Cenário executado

O teste de API real criou perfil, organização, projeto, solicitação, demanda, tarefa e instrução v1. Em seguida comprovou:

1. append prematuro e movimento prematuro para `done` retornam `409` sem mutação;
2. bloqueio com motivo, retomada e prioridade crítica;
3. triagem válida e regressão inválida;
4. tentativa 1 iniciada, concluída com evidência e rejeitada por reviewer independente;
5. instrução v2 humana, imutável e vinculada à v1;
6. tentativa 2 iniciada, concluída e aprovada por reviewer independente;
7. tarefa final `done`, progresso `100/100/100`, tentativas `failed/completed` e seis eventos operacionais;
8. restart do Host no mesmo SQLite preserva `done` e `instructionVersion=2`.

## Gates

- `tools/backend/verify.sh`: exit code 0.
- Build Release: zero warnings e zero erros.
- Unit: 83/83.
- Integration: 22/22.
- Contract: 8/8.
- Recovery: 4/4.
- Architecture: 6/6.
- Concurrency: 3/3.
- Total: 126/126.
- OpenAPI regenerado e drift test cobre todos os endpoints de comando.
- `frontend/**` e `docs/frontend/**`: nenhuma alteração.
