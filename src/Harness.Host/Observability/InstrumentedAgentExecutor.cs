using System.Diagnostics;
using Harness.Modules.Agents.Application.Execution;

namespace Harness.Host.Observability;

internal sealed class InstrumentedAgentExecutor(IAgentExecutor inner) : IAgentExecutor
{
    internal IAgentExecutor Inner { get; } =
        inner ?? throw new ArgumentNullException(nameof(inner));

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var activity = PoseidonTelemetry.ActivitySource.StartActivity(
            "poseidon.agent.execute",
            ActivityKind.Client);
        activity?.SetTag("tenant_id", request.TenantId);
        activity?.SetTag("project_id", request.ProjectId);
        activity?.SetTag("conversation_id", request.ConversationId);
        activity?.SetTag("agent_id", request.AgentId);
        activity?.SetTag("gen_ai.operation.name", "chat");
        activity?.SetTag("gen_ai.request.model", request.Model);
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.ExecuteAsync(request, cancellationToken);
            var executor = NormalizeDimension(result.Executor);
            activity?.SetTag("agent.executor", executor);
            activity?.SetTag("agent.result", "completed");
            activity?.SetTag("gen_ai.client.operation.duration_ms", result.DurationMs);
            PoseidonTelemetry.RecordAgentExecution(
                executor,
                "completed",
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag("agent.result", "cancelled");
            PoseidonTelemetry.RecordAgentExecution(
                "unknown",
                "cancelled",
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("error.type", exception.GetType().FullName);
            activity?.SetTag("agent.result", "failed");
            PoseidonTelemetry.RecordAgentExecution(
                "unknown",
                "failed",
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            throw;
        }
    }

    private static string NormalizeDimension(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return "unknown";
        }

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.')
                ? value.ToLowerInvariant()
                : "unknown";
    }
}
