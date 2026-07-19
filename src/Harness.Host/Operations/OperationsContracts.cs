namespace Harness.Host.Operations;

public sealed record BackupHandle(string BackupId, DateTimeOffset CreatedAt);
public sealed record DiagnosticCheck(string Key, string State, string Detail);
public sealed record ProductDiagnostic(string Name, string Version, string Codename);
public sealed record DiagnosticsContract(
    ProductDiagnostic Product, string ApiMode, string RealtimeState,
    IReadOnlyList<DiagnosticCheck> Checks, DateTimeOffset GeneratedAt);
