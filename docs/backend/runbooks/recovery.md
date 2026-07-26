# Runbook de recuperação

## Objetivos e gatilhos

O baseline pessoal tem RPO de 24 horas e RTO de 60 minutos. O baseline servidor
tem RPO de 5 minutos, alinhado ao `archive_timeout`, e RTO de 30 minutos. Cada
ambiente pode exigir metas menores, nunca maiores sem aceite explícito de risco.

Acione este runbook quando o watchdog identificar lease expirada, heartbeat
ausente, tentativa órfã, retry parado, outbox pendente ou dead letter; ou quando
`/ready` falhar por banco, migração ou reconciliação. Antes de agir, registre
provider, tenant, projeto, execução, tentativa, último checkpoint e fencing token.
Não copie payloads nem valores de segredo para o incidente.

## Backups verificáveis

No modo pessoal, o endpoint `POST /api/v1/backups` cria uma cópia SQLite consistente
e auditada. Para manutenção offline, use o comando nativo `.backup` sobre a fonte
fechada e grave o resultado fora do diretório de dados. Restaure primeiro em uma
cópia temporária e execute `PRAGMA integrity_check`; nunca sobrescreva a única
cópia conhecida como boa.

No modo servidor, o compose de produção mantém WAL arquivado a cada cinco minutos.
Gere uma base consistente antes de mudanças relevantes:

```bash
docker compose -f infra/compose/production.compose.yaml \
  --profile backup run --rm postgres-basebackup
```

Copie base backups e WAL para armazenamento imutável fora do host. Um backup só é
válido depois de restore ensaiado, migrations idempotentes, cadeia do ledger
válida e `/ready` verde. Nunca registre a conexão PostgreSQL em argumento; ela é
injetada por secret file ou referência opaca.

## PITR do PostgreSQL

1. Drene o Host e impeça novas escritas.
2. Preserve o volume e o WAL atuais como evidência; não os reutilize como destino.
3. Restaure o último base backup íntegro em volume novo.
4. Configure `restore_command` para o arquivo WAL e `recovery_target_time` para o
   instante aprovado, anterior ao incidente.
5. Inicie PostgreSQL isolado e aguarde a promoção concluir.
6. Execute as migrations em modo idempotente e reconcilie o ledger por tenant.
7. Valide contagens, inbox/outbox, último checkpoint, RLS e `/ready`.
8. Troque o Host para o volume restaurado e mantenha o anterior intacto até o
   aceite operacional.

Se o ponto exato não for comprovável dentro do RPO, mantenha o serviço bloqueado e
escale; não avance para um estado estimado.

## Continuação exata de execução

1. Reidratar `durable_executions`, `durable_attempts`, inbox, outbox e checkpoints.
2. Confirmar que a tentativa interrompida continua `running`, mas sua lease venceu.
3. Executar o watchdog uma vez. Ele marca a tentativa antiga como abandonada e
   agenda retry; a segunda execução é idempotente e não reenfileira novamente.
4. Invalidar owner e fencing token antigos. Resultado tardio com o token anterior
   é descartado.
5. Adquirir a nova tentativa com fencing token estritamente maior e o mesmo
   `context_snapshot`/`GitCheckpointContext`.
6. Repetir o último checkpoint para provar idempotência e continuar somente no
   passo seguinte. Exemplo: após `step-3`, a retomada começa em `step-4`.
7. Completar os efeitos e reprocessar a outbox por idempotency key.
8. Reconciliar projeções e ledger. Sequência, `previous_hash` e `event_hash` devem
   fechar; qualquer divergência bloqueia a retomada.

Nunca edite o ledger para forçar continuidade. Ausência de checkpoint ou efeito
externo sem idempotency key exige dead letter e decisão humana.

## Prova e encerramento

A simulação canônica envia `SIGKILL` depois do terceiro checkpoint e comprova, em
SQLite e PostgreSQL, duas tentativas (`abandoned`, `completed`), seis checkpoints
sem lacuna, fencing crescente, inbox/outbox idempotentes e ledger íntegro:

```bash
tools/backend/dotnet.sh test tests/Harness.RecoveryTests/Harness.RecoveryTests.csproj \
  --filter FullyQualifiedName~ProductionDurableExecutionRecoveryTests
tools/backend/verify-resilience.sh
```

Encerre apenas com `/ready` verde, reconciliação sem discrepâncias, backlog da
outbox drenado e evidências vinculadas ao incidente. Falha repetida, corrupção,
divergência dual ou RPO/RTO violado abre incidente crítico e mantém o gate fechado.
