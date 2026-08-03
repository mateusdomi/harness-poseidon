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

    /// <summary>
    /// Frases EXATAS de falta de credencial reconhecidas no diagnóstico (a cauda do erro
    /// padrão do executor). Nada de texto solto: "unauthorized" ou "login" aparecem em saída
    /// de trabalho e um falso positivo pararia a conta até um humano intervir. Estas duas
    /// formas são a frase que a CLI imprime quando a credencial não existe — observado ao
    /// vivo: a conta claude-secondary falhou dentro do contêiner (a credencial dela vive no
    /// Keychain do macOS, inalcançável de lá), saiu com `exit_code_1` e queimou o circuito de
    /// três cards saudáveis antes de alguém entender por quê.
    /// </summary>
    private static readonly string[] DiagnosticAuthSignals =
        ["not logged in", "please run /login"];

    /// <summary>
    /// A CONTA não consegue servir o modelo pedido — observado ao vivo: o backend do ChatGPT
    /// respondeu `The 'gpt-5-codex' model is not supported when using Codex with a ChatGPT
    /// account` para todos os modelos conhecidos do CLI instalado. Não é falha do card nem
    /// transitória: até um humano trocar plano, chave ou CLI, reeleger a conta é ruído, e
    /// classificar como permanente escalaria o CARD por culpa da conta. Casa tanto o código
    /// estruturado do adapter quanto a frase no diagnóstico.
    /// </summary>
    private static readonly string[] AccountModelSignals =
        ["account_model_unsupported", "not supported when using"];

    private static readonly string[] TransientSignals =
        ["timeout", "no_output", "tool_permission_denied", "prompt_write_failed", "start_failed",
         "connection", "reset", "econnreset", "socket", "temporarily", "overloaded", "503", "502", "exit_code"];

    /// <param name="failureDiagnostic">
    /// Cauda da saída de erro do executor, quando houver.
    ///
    /// Existe porque a CLI nem sempre traduz o erro do provedor em código estruturado: o
    /// GLM/Z.AI, por exemplo, imprime `rate_limit_error ... Usage limit reached` e sai com
    /// código 1, e o que chegava aqui era só `executor.exit_code_1` — que casa com o sinal
    /// `exit_code` da lista TRANSITÓRIA. Resultado observado: cota esgotada era tratada como
    /// instabilidade, a conta voltava do cooldown curto em minutos, era reeleita, queimava mais
    /// ~200s e derrubava outra rodada do card. Três dessas e um card saudável morria por um
    /// problema que era da CONTA.
    ///
    /// O diagnóstico é consultado para cota e para as frases EXATAS de falta de credencial
    /// (<see cref="DiagnosticAuthSignals"/>) — nunca para texto solto de "login": ela exige
    /// ação humana e não se recupera sozinha, então um falso positivo vindo do erro padrão
    /// pararia a conta até alguém intervir — pior que o defeito.
    /// </param>
    public static AgentRunOutcome Classify(
        ExternalAgentRunStatus status, string? failureCode, string? failureDiagnostic = null)
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
        if (Matches(code, QuotaSignals) ||
            Matches(failureDiagnostic ?? string.Empty, QuotaSignals))
        {
            return new AgentRunOutcome(AgentRunOutcomeKind.QuotaExhausted, "run.quota_exhausted", DefaultQuotaCooldown);
        }

        if (Matches(code, AuthSignals) ||
            Matches(failureDiagnostic ?? string.Empty, DiagnosticAuthSignals))
        {
            return new AgentRunOutcome(AgentRunOutcomeKind.AuthenticationRequired, "run.authentication_required", null);
        }

        if (Matches(code, AccountModelSignals) ||
            Matches(failureDiagnostic ?? string.Empty, AccountModelSignals))
        {
            return new AgentRunOutcome(
                AgentRunOutcomeKind.AuthenticationRequired, "run.account_model_unsupported", null);
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
