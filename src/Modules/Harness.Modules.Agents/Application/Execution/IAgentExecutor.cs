namespace Harness.Modules.Agents.Application.Execution;

public interface IAgentExecutor
{
    Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AgentExecutionRequest(
    string TenantId,
    string ProjectId,
    string ConversationId,
    string AgentId,
    string Instruction,
    string StatusDigestJson,
    string WorkingDirectory,
    string? SessionId = null,
    string? Model = null,
    string? Effort = null,
    string? CommunicationInstructions = null);

public sealed record AgentExecutionResult(
    string Executor,
    string SessionId,
    string TurnId,
    string StructuredOutput,
    IReadOnlyList<string> Chunks,
    long DurationMs);
