using System.Globalization;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Teto de um reset DECLARADO pelo provedor. Um texto corrompido não pode aposentar uma
    /// conta para sempre; uma janela semanal cabe folgada aqui.
    /// </summary>
    public static readonly TimeSpan MaximumQuotaCooldown = TimeSpan.FromDays(8);

    /// <summary>
    /// Instante de reset que o provedor ESCREVE na mensagem de cota. Observado ao vivo no
    /// GLM/Z.AI: `[1310][Weekly/Monthly Limit Exhausted. Your limit will reset at
    /// 2026-08-06 10:11:22]`. Sem ler esse instante, o cooldown padrão de três horas expirava,
    /// a conta voltava a ser elegível, era eleita e o card morria de novo — foi o que consumiu
    /// a madrugada de 2026-08-03: a cota era SEMANAL e o sistema tentava de três em três horas.
    /// </summary>
    private static readonly Regex ResetInstantPattern = new(
        @"reset(?:s)?\s+(?:at|em)\s+(\d{4}-\d{2}-\d{2})[ T](\d{2}:\d{2}(?::\d{2})?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    // Sinais OBSERVADOS nos adapters e nas CLIs: cota/limite, autenticação e falhas
    // transitórias (o GLM/Z.AI é instável e cai com reset de conexão / exit code).
    /// <summary>
    /// Hora LOCAL de reset, com o fuso nomeado, como a assinatura do Claude Code escreve:
    /// `You've hit your session limit · resets 11:30am (America/Sao_Paulo)`. Não há data: o
    /// reset é a PRÓXIMA ocorrência daquele horário naquele fuso.
    ///
    /// Sem ler isto, o limite de sessão caía no cooldown conservador de três horas — e a conta
    /// ficava fora da eleição bem depois de já ter voltado.
    /// </summary>
    private static readonly Regex ResetLocalTimePattern = new(
        @"reset(?:s)?\s+(?:at\s+|em\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)\s*\(([A-Za-z]+/[A-Za-z_+\-]+)\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    private static readonly string[] QuotaSignals =
        ["quota", "rate_limit", "ratelimit", "rate-limit", "resource_exhausted", "429", "usage_limit", "over_capacity",
         // O limite de SESSÃO da assinatura não traz código de cota nenhum: a CLI escreve a
         // frase e sai com 1. Ancorado na expressão inteira porque "limit" sozinho aparece em
         // trabalho legítimo sobre limites — e um falso positivo aqui tira uma conta boa da
         // eleição até o relógio virar.
         "session limit", "limite de sessão"];

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
        [
            "account_model_unsupported", "not supported when using",
            // INC-EVAL-001: a CLI Claude Code recusa `--model`/`--effort` que a CONTA específica
            // não aceita (roteada por um ANTHROPIC_BASE_URL alternativo) com estas frases — o
            // adaptador já classifica estruturalmente (ClaudeStreamJsonParser.ObserveFailureText);
            // estes sinais são o fallback de texto para quem não passar pelo caminho estruturado.
            "invalid model selection", "is not supported for model", "not recognized as a known model",
        ];

    private static readonly string[] TransientSignals =
        ["timeout", "no_output", "no_progress", "tool_permission_denied", "prompt_write_failed", "start_failed",
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
    /// <param name="now">
    /// Agora, para converter um reset declarado em cooldown. Injetável para o teste não
    /// depender do relógio.
    /// </param>
    /// <summary>
    /// Classifica pelo TIPO declarado pelo adaptador. É este o caminho correto: quem conhece o
    /// fornecedor traduz a peculiaridade dele; quem decide não conhece fornecedor nenhum.
    ///
    /// A heurística de texto só entra quando o adaptador diz <see cref="ExternalFailureKind.Unknown"/>
    /// — ou seja, quando ele PRÓPRIO não soube classificar. Enquanto houver executor não
    /// migrado esse caminho continua valendo; quando não houver mais, ele morre sem cerimônia.
    /// </summary>
    public static AgentRunOutcome Classify(
        ExternalAgentRunStatus status,
        ExternalFailureKind kind,
        string? failureCode,
        string? failureDiagnostic = null,
        DateTimeOffset? now = null)
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

        return kind switch
        {
            // Cota NUNCA é problema humano: o provedor diz quando volta, e o sistema espera
            // exatamente até lá. Decisão do proprietário em 2026-08-03 — "se está sem cota não
            // há o que eu possa fazer; quem monitora e chama de volta é você". Devolver o
            // cooldown genérico aqui seria jogar fora o reset que o adaptador já leu, que é
            // justamente o que transforma espera em desperdício.
            ExternalFailureKind.QuotaExhausted => new(
                AgentRunOutcomeKind.QuotaExhausted,
                "run.quota_exhausted",
                ResolveQuotaCooldown(failureDiagnostic, failureCode, now ?? DateTimeOffset.UtcNow)),
            ExternalFailureKind.AuthenticationRequired => new(
                AgentRunOutcomeKind.AuthenticationRequired, "run.authentication_required", null),
            ExternalFailureKind.AccountModelUnsupported => new(
                AgentRunOutcomeKind.AuthenticationRequired, "run.account_model_unsupported", null),
            ExternalFailureKind.Transient => new(
                AgentRunOutcomeKind.Transient, "run.transient_failure", null),
            ExternalFailureKind.Cancelled => new(
                AgentRunOutcomeKind.Cancelled, "run.cancelled", null),
            ExternalFailureKind.Timeout => new(
                AgentRunOutcomeKind.Transient, "run.timeout", null),
            ExternalFailureKind.Permanent => new(
                AgentRunOutcomeKind.Permanent, "run.permanent_failure", null),

            // O adaptador não soube dizer. SÓ aqui a leitura de texto ainda decide.
            _ => Classify(status, failureCode, failureDiagnostic, now),
        };
    }

    public static AgentRunOutcome Classify(
        ExternalAgentRunStatus status,
        string? failureCode,
        string? failureDiagnostic = null,
        DateTimeOffset? now = null)
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
            return new AgentRunOutcome(
                AgentRunOutcomeKind.QuotaExhausted,
                "run.quota_exhausted",
                ResolveQuotaCooldown(failureDiagnostic, failureCode, now ?? DateTimeOffset.UtcNow));
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

    /// <summary>
    /// Cooldown de cota: o instante que o PROVEDOR declarou, quando ele declara; senão o
    /// padrão conservador.
    ///
    /// O texto do provedor não traz fuso (o GLM/Z.AI escreve no horário dele, UTC+8). Ler como
    /// UTC deixa a conta parada por algumas horas A MAIS que o necessário — erro na direção
    /// segura. O erro na outra direção é o que já custou uma madrugada: voltar cedo, ser
    /// eleita e matar o card de novo.
    /// </summary>
    internal static TimeSpan ResolveQuotaCooldown(
        string? failureDiagnostic, string? failureCode, DateTimeOffset now)
    {
        foreach (var source in new[] { failureDiagnostic, failureCode })
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            Match match;
            try
            {
                match = ResetInstantPattern.Match(source);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (!match.Success ||
                !DateTimeOffset.TryParse(
                    $"{match.Groups[1].Value}T{match.Groups[2].Value}Z",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var reset))
            {
                continue;
            }

            var window = reset - now;
            if (window <= TimeSpan.Zero)
            {
                // Reset já passado: a cota deveria ter voltado. Um cooldown curto e novo
                // teste vale mais que confiar no texto.
                return DefaultQuotaCooldown;
            }

            return window > MaximumQuotaCooldown ? MaximumQuotaCooldown : window;
        }

        foreach (var source in new[] { failureDiagnostic, failureCode })
        {
            if (ResolveLocalReset(source, now) is { } localWindow)
            {
                return localWindow;
            }
        }

        return DefaultQuotaCooldown;
    }

    /// <summary>
    /// Converte um reset escrito em hora LOCAL com fuso nomeado na janela até a PRÓXIMA
    /// ocorrência dele. Devolve <see langword="null"/> quando o texto não traz esse formato ou
    /// quando o fuso é desconhecido nesta máquina — nesses casos o cooldown conservador é a
    /// resposta certa, e não um instante inventado.
    /// </summary>
    private static TimeSpan? ResolveLocalReset(string? source, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        Match match;
        try
        {
            match = ResetLocalTimePattern.Match(source);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var hour) ||
            hour is < 1 or > 12)
        {
            return null;
        }

        var minute = 0;
        if (match.Groups[2].Success &&
            (!int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out minute) ||
             minute is < 0 or > 59))
        {
            return null;
        }

        var isAfternoon = string.Equals(match.Groups[3].Value, "pm", StringComparison.OrdinalIgnoreCase);
        hour = hour % 12 + (isAfternoon ? 12 : 0);

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(match.Groups[4].Value);
        }
        catch (Exception exception) when (
            exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }

        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = new DateTimeOffset(
            localNow.Year, localNow.Month, localNow.Day, hour, minute, 0, localNow.Offset);
        if (candidate <= now)
        {
            // O horário de hoje já passou: o provedor está falando do de amanhã.
            candidate = candidate.AddDays(1);
        }

        var window = candidate - now;
        return window > MaximumQuotaCooldown ? MaximumQuotaCooldown : window;
    }

    private static bool Matches(string code, string[] signals) =>
        signals.Any(signal => code.Contains(signal, StringComparison.OrdinalIgnoreCase));
}
