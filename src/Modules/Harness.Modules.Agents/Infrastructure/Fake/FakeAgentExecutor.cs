using System.Diagnostics;
using System.Text.Json;
using Harness.Modules.Agents.Application.Execution;

namespace Harness.Modules.Agents.Infrastructure.Fake;

public sealed class FakeAgentExecutor : IAgentExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);
        var started = Stopwatch.GetTimestamp();
        var subject = request.Instruction.Trim();
        if (subject.Length > 160)
        {
            subject = string.Concat(subject.AsSpan(0, 157), "...");
        }

        string[] chunks =
        [
            "Recebi sua mensagem. ",
            $"O turno foi registrado de forma durável para: {subject}",
        ];
        var structured = JsonSerializer.Serialize(
            new { response = string.Concat(chunks), demands = Array.Empty<object>() },
            JsonOptions);
        _ = ChiefTurnOutputContract.Parse(structured);
        return Task.FromResult(new AgentExecutionResult(
            "fake",
            request.SessionId ?? $"fake:{request.ProjectId}",
            $"fake:{request.ConversationId}",
            structured,
            chunks,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
    }

    private static void Validate(AgentExecutionRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Instruction);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StatusDigestJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
    }
}
