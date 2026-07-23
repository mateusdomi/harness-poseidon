using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Conversations.Application;
using Harness.Modules.Governance.Context;
using Harness.Modules.Governance.Evaluation;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Cockpit;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Governance;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Workers;

public sealed partial class ChiefTurnBackgroundService(
    IChiefTurnStore turns,
    ICockpitDigestStore digests,
    IGovernanceRuntimeStore governance,
    ContextBundleBuilder bundleBuilder,
    IFreshContextEvaluator evaluator,
    IAgentExecutor executor,
    IClock clock,
    ChiefTurnWorkerOptions options,
    ChiefContextComposer contextComposer,
    ChiefContextStrategyOptions contextStrategyOptions,
    ILogger<ChiefTurnBackgroundService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _ownerId = $"chief-worker:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval);
        do
        {
            while (await ProcessNextAsync(stoppingToken))
            {
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A durable mailbox worker must isolate one failed agent turn and persist a retry decision.")]
    private async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var lease = await turns.AcquireNextAsync(
            _ownerId, clock.UtcNow, options.LeaseDuration, cancellationToken);
        if (lease is null) return false;
        GovernanceTurnReceiptRecord? receipt = null;
        try
        {
            var digest = await digests.ReadAsync(
                lease.Turn.TenantId, lease.Turn.ProjectId, 20, cancellationToken);
            var digestJson = JsonSerializer.Serialize(digest, JsonOptions);
            var bundle = bundleBuilder.BuildOrFallback(new ContextBundleRequest(
                lease.Turn.TenantId,
                lease.Turn.ProjectId,
                lease.Turn.TurnId,
                lease.Turn.TurnId,
                lease.ChiefAgentId,
                "poseidon",
                lease.Turn.Selection?.ModelName,
                "chief-turn",
                "execution",
                "orchestration",
                "medium",
                [],
                digestJson,
                ["Return a schema-valid Chief response.", "Persist durable completion evidence."],
                ["Domain writes only through typed Host stores."],
                [],
                ["Stop on canonical conflict, secret risk, invalid output or failed gate."],
                options.ContextBundlesEnabled ? options.ContextTokenBudget : 256));
            receipt = await governance.CreateReceiptAsync(
                new GovernanceTurnReceiptCreateCommand(
                    lease.Turn.TenantId,
                    lease.Turn.ProjectId,
                    lease.Turn.TurnId,
                    lease.Turn.TurnId,
                    lease.Turn.TurnId,
                    lease.ChiefAgentId,
                    bundle.ManifestVersion,
                    bundle.Documents.Select(document => new GovernanceReceiptDocumentRecord(
                        document.DocumentId,
                        document.Checksum,
                        document.SelectionReason,
                        document.LoadPolicy.ToString(),
                        document.EstimatedTokens)).ToArray(),
                    bundle.EstimatedTokens,
                    bundle.Truncated,
                    bundle.Conflicts,
                    bundle.CacheHits,
                    lease.Turn.Selection?.Source ?? "poseidon",
                    lease.Turn.Selection?.ModelName,
                    clock.UtcNow,
                    bundle.BundleChecksum),
                cancellationToken);
            await AppendBundleMetricsAsync(governance, lease, bundle, clock.UtcNow, cancellationToken);
            if (bundle.Conflicts.Count > 0)
            {
                throw new ContextBundleConflictException(bundle.Conflicts);
            }

            receipt = await governance.CompleteReceiptAsync(
                new GovernanceTurnReceiptCompleteCommand(
                    lease.Turn.TenantId,
                    lease.Turn.TurnId,
                    receipt.Version,
                    null,
                    GovernanceReceiptState.Delivered,
                    null,
                    clock.UtcNow),
                cancellationToken);
            // PLAT-02: quando a estratégia de contexto está ligada, monta a janela de trabalho
            // limitada (compactação + limpeza de tool-result) e externaliza os fatos críticos como
            // notas duráveis ANTES de invocar o modelo. Desligada (default), o contexto governado é
            // byte a byte idêntico ao comportamento anterior — nenhuma regressão.
            string governedDigestJson;
            if (contextStrategyOptions.Enabled)
            {
                var composition = await contextComposer.ComposeAsync(
                    lease.Turn.TenantId,
                    lease.Turn.ProjectId,
                    lease.Turn.ConversationId,
                    lease.Turn.TurnId,
                    cancellationToken);
                governedDigestJson = JsonSerializer.Serialize(
                    new ChiefGovernanceContextWithMemory(
                        digestJson,
                        bundle.RenderedContext,
                        bundle.BundleChecksum,
                        composition.RenderedContext,
                        composition.PersistedNoteCount),
                    JsonOptions);
            }
            else
            {
                governedDigestJson = JsonSerializer.Serialize(
                    new ChiefGovernanceContext(digestJson, bundle.RenderedContext, bundle.BundleChecksum),
                    JsonOptions);
            }
            var execution = await executor.ExecuteAsync(
                new AgentExecutionRequest(
                    lease.Turn.TenantId,
                    lease.Turn.ProjectId,
                    lease.Turn.ConversationId,
                    lease.ChiefAgentId,
                    lease.Instruction,
                    governedDigestJson,
                    AppContext.BaseDirectory,
                    lease.SessionId,
                    lease.Turn.Selection?.ModelName,
                    lease.Turn.Selection?.ProviderEffortValue),
                cancellationToken);
            var output = ChiefTurnOutputContract.Parse(execution.StructuredOutput);
            var evaluation = evaluator.Evaluate(
                new FreshContextEvaluationRequest(
                    $"evaluation:{lease.Turn.TurnId}",
                    lease.Turn.TenantId,
                    lease.Turn.ProjectId,
                    lease.Turn.TurnId,
                    lease.Turn.TurnId,
                    lease.ChiefAgentId,
                    $"{lease.ChiefAgentId}:critic",
                    "medium",
                    ["Chief output must conform to the structured contract."],
                    execution.StructuredOutput,
                    [$"executor={execution.Executor};durationMs={execution.DurationMs}"],
                    [new EvaluationTestResult("structured-output", true, "ChiefTurnOutputContract.Parse")]),
                clock.UtcNow);
            await governance.AppendMetricAsync(
                Metric(lease, GovernanceMetricKind.EvaluatorVerdict, null, null,
                    evaluation.Verdict.ToString().ToLowerInvariant(), clock.UtcNow),
                cancellationToken);
            if (evaluation.Verdict == EvaluationVerdict.Fail)
            {
                throw new AgentOutputValidationException("Independent evaluator returned Default-FAIL.");
            }
            var chunks = execution.Chunks.Count == 0 ? new[] { output.Response } : execution.Chunks;
            var occurredAt = clock.UtcNow;
            var message = ConversationApplicationService.CreateChiefMessage(
                UlidValue.New(occurredAt).ToString(),
                lease.Turn.ConversationId,
                lease.ChiefAgentId,
                output.Response,
                occurredAt);
            var demandSeeds = output.Demands
                .Select((demand, index) => new ChiefDemandSeed(
                    UlidValue.New(occurredAt.AddMilliseconds(10 + index * 2)).ToString(),
                    UlidValue.New(occurredAt.AddMilliseconds(11 + index * 2)).ToString(),
                    demand.Title,
                    demand.Description,
                    demand.RiskTier,
                    demand.AcceptanceCriteria))
                .ToArray();
            await turns.CompleteAsync(
                new ChiefTurnCompleteCommand(
                    lease,
                    new MessageRecord(
                        lease.Turn.TenantId, lease.Turn.ProjectId, message.Id,
                        message.ConversationId, message.AuthorRole, message.AuthorProfileId,
                        message.AuthorAgentId, message.Content, message.TokenCount, message.CreatedAt),
                    chunks,
                    execution.SessionId,
                    digestJson,
                    occurredAt,
                    demandSeeds),
                cancellationToken);
            receipt = await governance.CompleteReceiptAsync(
                new GovernanceTurnReceiptCompleteCommand(
                    lease.Turn.TenantId,
                    lease.Turn.TurnId,
                    receipt.Version,
                    null,
                    GovernanceReceiptState.Completed,
                    "pass",
                    clock.UtcNow),
                cancellationToken);
            await governance.AppendMetricAsync(
                Metric(lease, GovernanceMetricKind.GateResult, null, null, "pass", clock.UtcNow),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (receipt is not null && receipt.State is not GovernanceReceiptState.Completed and not GovernanceReceiptState.Failed)
            {
                receipt = await governance.CompleteReceiptAsync(
                    new GovernanceTurnReceiptCompleteCommand(
                        lease.Turn.TenantId,
                        lease.Turn.TurnId,
                        receipt.Version,
                        null,
                        GovernanceReceiptState.Failed,
                        "fail",
                        clock.UtcNow),
                    cancellationToken);
                await governance.AppendMetricAsync(
                    Metric(lease, GovernanceMetricKind.GateResult, null, null, "fail", clock.UtcNow),
                    cancellationToken);
            }
            var retryable = exception is not AgentOutputValidationException;
            await turns.FailAsync(
                new ChiefTurnFailCommand(
                    lease, exception.GetType().Name, clock.UtcNow, retryable),
                cancellationToken);
            LogTurnFailure(
                logger,
                lease.Turn.TurnId,
                exception.GetType().Name,
                retryable);
        }

        return true;
    }

    private static async Task AppendBundleMetricsAsync(
        IGovernanceRuntimeStore store,
        ChiefTurnLease lease,
        ContextBundle bundle,
        DateTimeOffset now,
        CancellationToken token)
    {
        var index = 0;
        foreach (var document in bundle.Documents)
        {
            await store.AppendMetricAsync(
                Metric(lease, GovernanceMetricKind.Selected, document.DocumentId, null, null,
                    now.AddTicks(index++)), token);
        }

        foreach (var documentId in bundle.Truncated)
        {
            await store.AppendMetricAsync(
                Metric(lease, GovernanceMetricKind.ItemTruncated, documentId, null, null,
                    now.AddTicks(index++)), token);
        }

        await store.AppendMetricAsync(
            Metric(lease, GovernanceMetricKind.Delivered, null, null, bundle.BundleChecksum,
                now.AddTicks(index)), token);
    }

    private static GovernanceMetricAppendCommand Metric(
        ChiefTurnLease lease,
        GovernanceMetricKind kind,
        string? documentId,
        string? ruleId,
        string? detail,
        DateTimeOffset at) => new(
        lease.Turn.TenantId,
        lease.Turn.ProjectId,
        lease.Turn.TurnId,
        UlidValue.New(at).ToString(),
        kind,
        documentId,
        ruleId,
        detail,
        null,
        at);

    private sealed record ChiefGovernanceContext(
        string StatusDigestJson,
        string ContextBundle,
        string BundleChecksum);

    private sealed record ChiefGovernanceContextWithMemory(
        string StatusDigestJson,
        string ContextBundle,
        string BundleChecksum,
        string ChiefContext,
        int PersistedNoteCount);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Warning,
        Message = "Chief turn {TurnId} failed with {ErrorType}; retryable={Retryable}.")]
    private static partial void LogTurnFailure(
        ILogger logger,
        string turnId,
        string errorType,
        bool retryable);
}
