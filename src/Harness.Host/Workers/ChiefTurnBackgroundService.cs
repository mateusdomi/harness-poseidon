using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Conversations.Application;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Cockpit;
using Harness.Persistence.Abstractions.Conversations;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Workers;

public sealed partial class ChiefTurnBackgroundService(
    IChiefTurnStore turns,
    ICockpitDigestStore digests,
    IAgentExecutor executor,
    IClock clock,
    ChiefTurnWorkerOptions options,
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
        try
        {
            var digest = await digests.ReadAsync(
                lease.Turn.TenantId, lease.Turn.ProjectId, 20, cancellationToken);
            var digestJson = JsonSerializer.Serialize(digest, JsonOptions);
            var execution = await executor.ExecuteAsync(
                new AgentExecutionRequest(
                    lease.Turn.TenantId,
                    lease.Turn.ProjectId,
                    lease.Turn.ConversationId,
                    lease.ChiefAgentId,
                    lease.Instruction,
                    digestJson,
                    AppContext.BaseDirectory,
                    lease.SessionId,
                    lease.Turn.Selection?.ModelName,
                    lease.Turn.Selection?.ProviderEffortValue),
                cancellationToken);
            var output = ChiefTurnOutputContract.Parse(execution.StructuredOutput);
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
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
