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
        var demands = request.Instruction
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("DEMANDA:", StringComparison.Ordinal))
            .Select(line =>
            {
                var parts = line["DEMANDA:".Length..].Split('|', 2, StringSplitOptions.TrimEntries);
                return new FakeDemandProposal(
                    parts[0],
                    parts.Length > 1 && parts[1].Length > 0 ? parts[1] : parts[0],
                    "medium",
                    ["Critério de aceite proposto pelo Chief."]);
            })
            .ToArray();
        var structured = JsonSerializer.Serialize(
            new FakeChiefOutput(string.Concat(chunks), demands),
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

    private sealed record FakeChiefOutput(string Response, IReadOnlyList<FakeDemandProposal> Demands);

    private sealed record FakeDemandProposal(
        string Title,
        string Description,
        string RiskTier,
        IReadOnlyList<string> AcceptanceCriteria);

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
