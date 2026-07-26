namespace Harness.SharedKernel.Providers;

/// <summary>
/// Registro de uma invocação de modelo LLM para auditoria de cota, custo e telemetria (Fase 3 / N4).
/// </summary>
public sealed record ModelInvocationRecord(
    string Id,
    string TenantId,
    string ProjectId,
    string WorkTaskId,
    string AttemptId,
    string Provider,
    string Model,
    string AccountAlias,
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCostUsd,
    long DurationMs,
    string Outcome,
    DateTimeOffset InvokedAt);
