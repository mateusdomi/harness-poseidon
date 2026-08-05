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
    DateTimeOffset InvokedAt,

    /// <summary>O modelo que a ROTA pediu (Onda 0.4). Vazio = rota sem preferência.</summary>
    string RequestedModel = "",

    /// <summary>O esforço que a rota pediu. Vazio = rota sem preferência.</summary>
    string RequestedEffort = "",

    /// <summary>O modelo que a CLI efetivamente recebeu. Vazio = default do provedor.</summary>
    string ResolvedModel = "",

    /// <summary>
    /// O esforço que a CLI efetivamente recebeu. Pedido ≠ recebido é FATO auditável aqui —
    /// era um drop silencioso no despacho.
    /// </summary>
    string ResolvedEffort = "");
