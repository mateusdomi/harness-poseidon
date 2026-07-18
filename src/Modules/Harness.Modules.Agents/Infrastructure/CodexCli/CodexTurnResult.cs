namespace Harness.Modules.Agents.Infrastructure.CodexCli;

public sealed record CodexTurnResult(
    string ThreadId,
    string TurnId,
    string Status,
    string FinalMessage,
    IReadOnlyList<string> Deltas,
    long DurationMs);
