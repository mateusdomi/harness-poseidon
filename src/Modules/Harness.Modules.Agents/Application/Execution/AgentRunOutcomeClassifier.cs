using Harness.Modules.Agents.Application.Execution.External;

namespace Harness.Modules.Agents.Application.Execution;

/// <summary>
/// Categoria fechada do desfecho de uma execução de agente — a base do retry inteligente e
/// do agendamento de retomada. Distingue o que ADIA (cota, login), o que REPETE (falha
/// transitória) e o que ESCALA (permanente).
/// </summary>
public enum AgentRunOutcomeKind
{
    /// <summary>Concluiu com sucesso.</summary>
    Completed,

    /// <summary>Cancelado por decisão explícita — não repetir.</summary>
    Cancelled,

    /// <summary>Cota/limite esgotado: aguardar o reset da janela antes de retomar.</summary>
    QuotaExhausted,

    /// <summary>Credencial ausente/expirada: exige login humano — não repetir sozinho.</summary>
    AuthenticationRequired,

    /// <summary>Falha transitória (rede instável, timeout, saída vazia): candidata a retry com backoff.</summary>
    Transient,

    /// <summary>Falha determinística do trabalho: escala para revisão humana, não repete cega.</summary>
    Permanent,
}

/// <summary>
/// Desfecho classificado. <see cref="SuggestedCooldown"/> é uma sugestão CONSERVADORA quando
/// a janela real de reset não é conhecida (a maioria das CLIs não a expõe headless); nunca é
/// um número inventado de cota, apenas quando voltar a TENTAR.
/// </summary>
public sealed record AgentRunOutcome(
    AgentRunOutcomeKind Kind,
    string ReasonCode,
    TimeSpan? SuggestedCooldown)
{
    public bool ShouldRetry => Kind is AgentRunOutcomeKind.Transient;

    public bool ShouldWaitForReset => Kind is AgentRunOutcomeKind.QuotaExhausted;

    public bool NeedsHuman => Kind is AgentRunOutcomeKind.AuthenticationRequired or AgentRunOutcomeKind.Permanent;
}

/// <summary>
/// Classifica o desfecho a partir do status e do código de falha OBSERVADOS do executor.
/// A classificação é por padrões de código reais (nunca adivinha a marca do provider): cota,
/// login, transitório e permanente. Um cooldown conservador padrão é sugerido para cota
/// quando a janela real não é conhecida.
/// </summary>
public static class AgentRunOutcomeClassifier
{
    /// <summary>Cooldown conservador padrão quando a CLI não expõe a janela de reset.</summary>
    public static readonly TimeSpan DefaultQuotaCooldown = TimeSpan.FromHours(3);

    // Sinais OBSERVADOS nos adapters e nas CLIs: cota/limite, autenticação e falhas
    // transitórias (o GLM/Z.AI é instável e cai com reset de conexão / exit code).
    private static readonly string[] QuotaSignals =
        ["quota", "rate_limit", "ratelimit", "rate-limit", "resource_exhausted", "429", "usage_limit", "over_capacity"];

    private static readonly string[] AuthSignals =
        ["authentication_required", "not_logged_in", "logged in", "log in", "unauthorized",
         "401", "invalid_api_key", "/login"];

    private static readonly string[] TransientSignals =
        ["timeout", "no_output", "tool_permission_denied", "prompt_write_failed", "start_failed",
         "connection", "reset", "econnreset", "socket", "temporarily", "overloaded", "503", "502", "exit_code"];

    public static AgentRunOutcome Classify(ExternalAgentRunStatus status, string? failureCode)
    {
        switch (status)
        {
            case ExternalAgentRunStatus.Completed:
                return new AgentRunOutcome(AgentRunOutcomeKind.Completed, "run.completed", null);
            case ExternalAgentRunStatus.Cancelled:
                return new AgentRunOutcome(AgentRunOutcomeKind.Cancelled, "run.cancelled", null);
            case ExternalAgentRunStatus.TimedOut:
                return new AgentRunOutcome(AgentRunOutcomeKind.Transient, "run.timeout", null);
            default:
                break;
        }

        var code = failureCode ?? string.Empty;

        // Ordem: cota e login primeiro (adiam/escalam), depois transitório (repete), senão permanente.
        if (Matches(code, QuotaSignals))
        {
            return new AgentRunOutcome(AgentRunOutcomeKind.QuotaExhausted, "run.quota_exhausted", DefaultQuotaCooldown);
        }

        if (Matches(code, AuthSignals))
        {
            return new AgentRunOutcome(AgentRunOutcomeKind.AuthenticationRequired, "run.authentication_required", null);
        }

        if (Matches(code, TransientSignals))
        {
            return new AgentRunOutcome(AgentRunOutcomeKind.Transient, "run.transient_failure", null);
        }

        return new AgentRunOutcome(AgentRunOutcomeKind.Permanent, "run.permanent_failure", null);
    }

    private static bool Matches(string code, string[] signals) =>
        signals.Any(signal => code.Contains(signal, StringComparison.OrdinalIgnoreCase));
}
