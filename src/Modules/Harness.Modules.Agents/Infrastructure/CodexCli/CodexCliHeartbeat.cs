namespace Harness.Modules.Agents.Infrastructure.CodexCli;

public sealed record CodexCliHeartbeat(int ProcessId, long Sequence, DateTimeOffset OccurredAt);
