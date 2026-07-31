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

## Planejamento incompleto de uma demanda

Um turno do Chefe que delega demandas grava, na MESMA transação que o conclui, o
compromisso `demand_materializations` (estado `pending`) e o comando de outbox
`plan.materializationRequested`. Enquanto o compromisso não estiver `completed`, o
planejamento daquela demanda está declaradamente incompleto — a resposta ao usuário
já existe, os cards ainda não.

1. Ler o compromisso: `pending` (comando não consumido), `processing` (dono vivo ou
   morto, conforme `updated_at` contra o lease), `failed` (`last_error` tipado) ou
   `completed`.
2. `completed` não é palavra final: comparar as fatias previstas do plano com
   `work_tasks.plan_id`/`plan_slice_key`. Divergência significa board alterado por
   fora, e o reconciliador reabre o compromisso.
3. O reconciliador roda no boot e a cada ciclo. Ele CONVERGE — recria apenas as
   fatias ausentes, nunca duplica e só carimba `demand_plans.materialized_at`
   quando o conjunto está completo e todas as dependências declaradas resolvem.
4. Falha terminal (`plan_dependency_unresolved`, `plan_slice_key_duplicated`,
   `demand_missing`, `loop_guard_interrupted`) não é retentada: corrija o plano ou
   a demanda; insistir só produziria ruído.
5. Nunca carimbe `materialized_at` à mão para "destravar": o marker é a afirmação
   de que os cards existem, e forçá-lo recria exatamente o defeito BR-001.

```bash
tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj \
  --filter FullyQualifiedName~PlanMaterializationDurabilityTests
```

## Turno do Chefe duplicado ou sem contexto recente

O lease do turno é RENOVADO pelo batimento (`TryRenewAsync`), condicionado ao par (dono, fencing).
Uma inferência mais longa que o lease deixou de órfanizar o turno vivo.

1. Turno aparentemente executado duas vezes: confira `poseidon.chief.turn.lease.count` por
   `outcome`. `lost` significa que um worker descobriu o fencing perdido e ABORTOU sem escrever —
   é o comportamento correto, não um erro. Reincidência aponta para renovação lenta: compare
   `LeaseDuration` com `ActivityHeartbeatInterval` (o mínimo exigido é o triplo).
2. Turno preso: o lease NÃO bloqueia recuperação. Sem renovação, ele vence e outro worker adquire
   com fencing maior. Se um turno segue `processing` sem dono vivo, verifique se o processo antigo
   continua rodando antes de mexer no banco.
3. A Bruna respondendo com contexto antigo: o histórico é lido pelas ÚLTIMAS N mensagens
   (`ListRecentMessagesAsync`), com o mandato fundador preservado à parte e as notas externalizadas
   reinjetadas com proveniência. Se o contexto voltar a parecer antigo, cheque `HistoryScanLimit`,
   o orçamento de tokens e se `chief_context_notes` está sendo lido — nota gravada e nunca lida é
   esquecimento, não memória.

```bash
tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj \
  --filter "FullyQualifiedName~ChiefTurnLeaseRenewalTests|FullyQualifiedName~ChiefLongProjectContextTests"
```

## Segredo em canal de evidência

A sanitização acontece ANTES da persistência, não na exportação. Redigir na saída é maquiagem: o
valor já está no disco, no ledger encadeado e em todo backup tirado desde então.

1. `PersistenceSanitizer` é a política única. Ela roda nos funis de escrita de auditoria e outbox de
   todos os stores, e antes do hash do ledger — o conteúdo verificado é o conteúdo persistido.
2. Canal de alto risco (`audit_ledger`) é fail-closed: se algo reconhecível sobreviver à
   sanitização, a escrita é RECUSADA (`SecretPersistenceException`) em vez de gravada "quase limpa".
3. FRONTEIRA DELIBERADA: o corpo que o humano escreveu (solicitação, demanda, instrução, mensagem)
   é dado de negócio e é preservado intacto — apagar trecho do texto que ele vai reler quebraria o
   produto e esconderia o próprio incidente. O que a sanitização impede é esse texto se multiplicar
   sem redação pelos canais de evidência e transporte.
4. Suspeita de vazamento: rode a varredura canário. Ela percorre TODAS as tabelas do banco, o
   backup e o dump — uma tabela nova que passe a guardar conteúdo livre sem sanitizar cai nela
   automaticamente.

```bash
tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj \
  --filter FullyQualifiedName~CanarySecretPersistenceTests
```

## Execução sem sandbox

`SandboxActive` deixou de ser literal: ele vem de uma attestation emitida pelo provider REAL da
tentativa, persistida em `sandbox_attestations`. A mera criação de um container não conta — a
attestation exige rootfs somente-leitura, worktree isolada, egresso negado e limites aplicados.

1. Execução negada com `sandbox_required`: leia a attestation da tentativa. O campo
   `verification_detail` diz o que faltou (runtime ausente, container inexistente, rede em bridge,
   limites não aplicados). Isso é fail-closed funcionando, não defeito.
2. A attestation é POR TENTATIVA e a PRIMEIRA emissão é a que vale — uma segunda não pode abençoar
   retroativamente uma execução em curso.
3. Modo inseguro: só existe por aceite do proprietário via sessão de perfil local
   (`POST /api/v1/projects/{id}/unsafe-execution`), com motivo obrigatório, validade máxima de 24h
   e auditoria. Nenhum agente tem caminho até esse registro; não há flag nem variável de ambiente.
   Revogue com `DELETE` no mesmo recurso — passa a valer imediatamente.
4. `poseidon.sandbox.attestation.count` por `provider`/`verified` mostra quanto do trabalho está
   correndo contido de verdade.

```bash
tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj \
  --filter FullyQualifiedName~SandboxAttestationTests
```

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
