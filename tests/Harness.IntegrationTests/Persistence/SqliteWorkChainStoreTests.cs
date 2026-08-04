using System.Security.Cryptography;
using System.Text;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Persistence;

public sealed class SqliteWorkChainStoreTests
{
    [Fact]
    public async Task CreationIsAtomicIdempotentAuditedAndReadable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f1-work-chain-sqlite",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(artifactRoot, "work-chain.db"),
                timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                FoundationTransactionBehavior.Command(),
                timeout.Token);
            var store = new SqliteWorkChainStore(dispatcher);
            await WorkChainStoreBehavior.AssertAsync(store, timeout.Token);
            await WorkChainStoreBehavior.AssertReviewUnavailableEscalationAsync(
                store, timeout.Token);
            await WorkChainStoreBehavior.AssertUndispatchableEscalationAsync(
                store, timeout.Token);
            await WorkChainStoreBehavior.AssertReplanClosesCircuitAsync(
                store,
                new SqliteCardCircuitBreakerStore(dispatcher),
                timeout.Token);

            // O consumo medido precisa chegar à projeção do quadro. Sem isso, custo, duração e
            // tokens aparecem zerados na interface mesmo com trabalho real executado — e a
            // auditoria não consegue responder quanto custou nem quanto demorou.
            var attempts = await new SqliteWorkBoardStore(dispatcher).ListAttemptsAsync(
                FoundationTransactionBehavior.TenantId,
                WorkChainStoreBehavior.UnreviewableTaskId,
                null,
                10,
                timeout.Token);
            var attempt = Assert.Single(attempts);
            Assert.Equal(12_345, attempt.DurationMs);
            Assert.Equal(900, attempt.TokensInput);
            Assert.Equal(350, attempt.TokensOutput);
            Assert.Equal(1.25m, attempt.CostUsd);

            // Falha TRANSITÓRIA lida pelo MESMO caminho do Chief (ChiefBacklogLoopService lê
            // ListAttemptsAsync e alimenta o circuito do card). O motivo precisa sobreviver à
            // escrita mesmo sem consumir rodada: 'cancelled' SEM motivo é indistinguível de um
            // reinício do Host, e foi assim que nove falhas seguidas no mesmo card não contaram
            // nenhuma. A combinação abaixo é a que `CardCircuitBreakerService.IsFailure` exige.
            var transient = Assert.Single(await new SqliteWorkBoardStore(dispatcher).ListAttemptsAsync(
                FoundationTransactionBehavior.TenantId,
                WorkChainStoreBehavior.TransientFailureTaskId,
                null,
                10,
                timeout.Token));
            Assert.Equal("cancelled", transient.State);
            Assert.Equal(WorkChainStoreBehavior.TransientFailureReason, transient.FailureReason);

            // Um card com MAIS tentativas que o limite não pode esconder as recentes.
            //
            // Com `ORDER BY a.id LIMIT` (ULID cresce com o tempo), a página devolvia as mais
            // ANTIGAS. Medido em produção: dois cards com 101 tentativas e limite 100 — a
            // tentativa EM EXECUÇÃO era a 101ª e sumia. A colheita não achava o run vivo, a
            // reconciliação não fechava a órfã, o circuito contava falhas de um passado
            // congelado e o card ficava preso por dias sem emitir um único sinal.
            var board = new SqliteWorkBoardStore(dispatcher);
            var pageOfTwo = await board.ListAttemptsAsync(
                FoundationTransactionBehavior.TenantId,
                WorkChainStoreBehavior.CrowdedTaskId,
                null,
                2,
                timeout.Token);

            Assert.Equal(2, pageOfTwo.Count);
            // As duas ÚLTIMAS, em ordem cronológica — o circuito depende dessa ordem.
            Assert.Equal(
                WorkChainStoreBehavior.CrowdedNewestAttemptId,
                pageOfTwo[^1].Id);
            Assert.DoesNotContain(
                pageOfTwo,
                attempt => attempt.Id == WorkChainStoreBehavior.CrowdedOldestAttemptId);
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReviewRejectionCauseIsPersistedAndReadBack()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f22-work-review-cause",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(artifactRoot, "work-chain.db"),
                timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                FoundationTransactionBehavior.Command(),
                timeout.Token);
            var store = new SqliteWorkChainStore(dispatcher);
            const string instruction = "Implement the immutable work-chain transaction.";
            var chain = new WorkChainCreateCommand(
                FoundationTransactionBehavior.TenantId,
                FoundationTransactionBehavior.ProjectId,
                "01ARZ3NDEKTSV4RRFFQ69G5FAY",
                "01ARZ3NDEKTSV4RRFFQ69G5FF0",
                "Build the first persisted work chain.",
                "01ARZ3NDEKTSV4RRFFQ69G5FF1",
                "Persist the demand",
                "[\"State is atomic\",\"Audit is complete\"]",
                "01ARZ3NDEKTSV4RRFFQ69G5FF2",
                "Create work-chain transaction",
                "low",
                5m,
                "01ARZ3NDEKTSV4RRFFQ69G5FF3",
                instruction,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instruction))),
                "work-chain:create:f22",
                new DateTimeOffset(2026, 7, 18, 16, 10, 0, TimeSpan.Zero));

            await store.CreateAsync(chain, cancellationToken: timeout.Token);

            var triage = new WorkTaskLifecycleCommand(
                chain.TenantId,
                chain.SolicitationId,
                chain.TaskId,
                "chief",
                "bruna",
                "Demand scope and risk were triaged.",
                "triage:accepted",
                1,
                "work-chain:task:triage:f22",
                chain.OccurredAt.AddSeconds(20));
            var triaged = await store.TriageTaskAsync(triage, timeout.Token);
            Assert.Equal(WorkChainMutationStatus.Applied, triaged.Status);

            var readiness = triage with
            {
                Reason = "Acceptance criteria and dependencies satisfy the Definition of Ready.",
                EvidenceReference = "dor:validated",
                ExpectedTaskVersion = triaged.TaskVersion!.Value,
                IdempotencyKey = "work-chain:task:ready:f22",
                OccurredAt = chain.OccurredAt.AddSeconds(30),
            };
            var ready = await store.MarkTaskReadyAsync(readiness, timeout.Token);
            Assert.Equal(WorkChainMutationStatus.Applied, ready.Status);

            var start = new WorkAttemptStartCommand(
                chain.TenantId,
                chain.SolicitationId,
                chain.TaskId,
                chain.InstructionVersionId,
                "01ARZ3NDEKTSV4RRFFQ69G5FF4",
                "worker-alias",
                ready.TaskVersion!.Value,
                "work-chain:attempt:start:f22",
                chain.OccurredAt.AddMinutes(1));
            var started = await store.StartAttemptAsync(start, timeout.Token);
            Assert.Equal(WorkChainMutationStatus.Applied, started.Status);

            var complete = new WorkAttemptCompleteCommand(
                chain.TenantId,
                chain.SolicitationId,
                chain.TaskId,
                start.AttemptId,
                started.TaskVersion!.Value,
                [new WorkEvidenceInput("01ARZ3NDEKTSV4RRFFQ69G5FF5", "evidence:f22")],
                "work-chain:attempt:complete:f22",
                chain.OccurredAt.AddMinutes(2));
            var completed = await store.CompleteAttemptAsync(complete, timeout.Token);
            Assert.Equal(WorkChainMutationStatus.Applied, completed.Status);

            var review = new WorkAttemptReviewCommand(
                chain.TenantId,
                chain.SolicitationId,
                chain.TaskId,
                start.AttemptId,
                UlidValue.New(chain.OccurredAt.AddMinutes(5)).ToString(),
                "independent-reviewer",
                "rejected",
                "Evidência insuficiente.",
                completed.TaskVersion!.Value,
                "work-chain:attempt:review:f22",
                chain.OccurredAt.AddMinutes(5))
            {
                RejectionCause = "contextMissing",
            };

            var reviewed = await store.ReviewAttemptAsync(review, timeout.Token);
            Assert.Equal(WorkChainMutationStatus.Applied, reviewed.Status);

            var aggregate = await store.ReadAggregateAsync(
                chain.TenantId, chain.SolicitationId, timeout.Token);
            Assert.NotNull(aggregate);
            var attempt = Assert.Single(Assert.Single(aggregate.Demands).Tasks).Attempts[^1];
            Assert.NotNull(attempt.Review);
            Assert.Equal("rejected", attempt.Review.Decision);
            Assert.Equal("contextMissing", attempt.Review.RejectionCause);
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }
}
