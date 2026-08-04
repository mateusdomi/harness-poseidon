namespace Harness.Modules.Workflows.Application;

/// <summary>
/// Modo de operação do projeto — quem decide a TRANSIÇÃO DE FASE. Conjunto fechado; um valor
/// desconhecido cai no modo mais conservador (manual), nunca no mais permissivo.
/// </summary>
public enum ProjectOperationMode
{
    /// <summary>A chefe decide o portão por evidência; o dono acompanha e não bloqueia.</summary>
    Autonomous,

    /// <summary>Autônomo por padrão; só os portões das fases escolhidas pelo dono esperam por ele.</summary>
    SemiAutonomous,

    /// <summary>Todo portão de transição espera decisão humana.</summary>
    Manual,
}

/// <summary>
/// O que fazer com o portão de uma fase AGORA. Separa três coisas que o produto vinha tratando
/// como uma só: o trabalho não terminou, o trabalho terminou e a chefe pode aprovar, e o trabalho
/// terminou mas a decisão é do dono.
/// </summary>
public enum PhaseGateDecision
{
    /// <summary>Faltam obrigações ou evidências: o portão não está pronto (Default-FAIL).</summary>
    NotReady,

    /// <summary>Pronto e dentro da autonomia configurada: a chefe aprova e a fase avança.</summary>
    ChiefApproves,

    /// <summary>Pronto, porém a transição desta fase foi reservada ao humano pelo próprio dono.</summary>
    AwaitHuman,
}

/// <summary>
/// Fatos objetivos sobre a fase, colhidos do estado real antes da decisão. Todos os campos são
/// medidos, nunca inferidos de texto: é isto que impede "Default-FAIL" de virar "humano sempre".
/// </summary>
public sealed record PhaseGateEvidence(
    bool HasGate,
    bool AllRequiredObligationsAccepted,
    bool HasBlockingFinding,
    bool HasOpenBlocker,
    int RequiredObligationCount,

    /// <summary>
    /// Veredito do Definition of Done do PRODUTO, quando a fase entrega software ao usuário.
    /// <see langword="null"/> significa "não se aplica a esta fase" — uma fase documental não
    /// precisa provar que existe frontend. Onde se aplica, um veredito reprovado mantém o portão
    /// fechado independentemente das obrigações documentais estarem aceitas: documento que afirma
    /// que o produto está pronto não é evidência de que ele está.
    /// </summary>
    Product.ProductDeliveryVerdict? ProductDelivery = null);

/// <summary>
/// Política PURA do portão de fase. É aqui que os três modos do produto deixam de ser um campo
/// guardado no catálogo e passam a governar o comportamento.
///
/// A regra que esta política existe para separar: <b>Default-FAIL não é o mesmo que aprovação
/// humana obrigatória</b>. Default-FAIL diz que, sem evidência suficiente, o portão REPROVA — e
/// isso vale nos três modos. Quem decide, quando a evidência existe, é o modo escolhido pelo dono.
///
/// Sem esta separação, o produto obrigava o stakeholder a aprovar cada fase de cada projeto, o que
/// transforma o dono em gargalo permanente de uma fábrica que existe justamente para não depender
/// dele nas decisões operacionais.
/// </summary>
public static class PhaseGatePolicy
{
    /// <summary>
    /// Lê o modo persistido. Valor ausente, vazio ou desconhecido resolve para
    /// <see cref="ProjectOperationMode.Manual"/>: diante de configuração que não entendemos, a
    /// escolha segura é devolver a decisão ao humano, nunca assumir autonomia.
    /// </summary>
    public static ProjectOperationMode ParseMode(string? mode) =>
        (mode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "autonomous" or "autonomo" or "autônomo" => ProjectOperationMode.Autonomous,
            "semiautonomous" or "semi-autonomous" or "semiautonomo" or "semiautônomo"
                => ProjectOperationMode.SemiAutonomous,
            _ => ProjectOperationMode.Manual,
        };

    public static string Serialize(ProjectOperationMode mode) => mode switch
    {
        ProjectOperationMode.Autonomous => "autonomous",
        ProjectOperationMode.SemiAutonomous => "semiautonomous",
        _ => "manual",
    };

    /// <summary>
    /// Decide o portão da fase.
    ///
    /// <paramref name="pauseGates"/> só tem efeito no modo semiautônomo e casa por NOME DE FASE ou
    /// NOME DE PORTÃO — o dono marca fases na tela, e os dois identificadores precisam funcionar
    /// para que a configuração dele não dependa de saber o vocabulário interno.
    /// </summary>
    public static PhaseGateDecision Decide(
        ProjectOperationMode mode,
        string phaseName,
        string? gateName,
        IReadOnlyList<string>? pauseGates,
        PhaseGateEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        // Default-FAIL, nos TRÊS modos: sem obrigações cumpridas, com achado impeditivo ou com
        // bloqueio aberto, o portão não está pronto — e uma fase sem nenhuma obrigação obrigatória
        // também não está: aprovar o vazio seria aprovar sem evidência nenhuma.
        if (!evidence.HasGate ||
            evidence.RequiredObligationCount == 0 ||
            !evidence.AllRequiredObligationsAccepted ||
            evidence.HasBlockingFinding ||
            evidence.HasOpenBlocker)
        {
            return PhaseGateDecision.NotReady;
        }

        // Definition of Done do produto. Onde a fase entrega software a uma pessoa, obrigação
        // documental cumprida não basta: um `GET /emprestimos` com todos os documentos da fase
        // aceitos continua não sendo um sistema de empréstimos.
        if (evidence.ProductDelivery is { Satisfied: false })
        {
            return PhaseGateDecision.NotReady;
        }

        return mode switch
        {
            ProjectOperationMode.Autonomous => PhaseGateDecision.ChiefApproves,
            ProjectOperationMode.SemiAutonomous => IsPaused(phaseName, gateName, pauseGates)
                ? PhaseGateDecision.AwaitHuman
                : PhaseGateDecision.ChiefApproves,
            _ => PhaseGateDecision.AwaitHuman,
        };
    }

    private static bool IsPaused(
        string phaseName, string? gateName, IReadOnlyList<string>? pauseGates)
    {
        if (pauseGates is null || pauseGates.Count == 0)
        {
            return false;
        }

        foreach (var entry in pauseGates)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var value = entry.Trim();
            if (string.Equals(value, phaseName, StringComparison.OrdinalIgnoreCase) ||
                (gateName is { Length: > 0 } && string.Equals(value, gateName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
