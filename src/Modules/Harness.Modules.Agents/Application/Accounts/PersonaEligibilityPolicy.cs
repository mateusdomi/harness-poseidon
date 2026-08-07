using System.Globalization;

namespace Harness.Modules.Agents.Application.Accounts;

/// <summary>O que se sabe de uma persona no momento de decidir se ela pode receber um card.</summary>
public sealed record PersonaEligibilityInput(
    string Key,
    string Role,
    bool Enabled,
    bool Archived,
    string LifecycleState,
    string? ScopeProjectId,
    string? Risk,
    IReadOnlyList<string> ToolIds,
    IReadOnlyList<string> MissingRequiredCapabilities);

/// <summary>Veredito com o motivo tipado: por que a persona pode (ou não) assumir o card.</summary>
public sealed record PersonaEligibilityVerdict(bool Eligible, string ReasonCode);

/// <summary>
/// Separa CRIAR uma persona de ela poder EXECUTAR um card.
///
/// A criação autônoma resolveu a lacuna de competência, mas criou um risco novo: uma persona no
/// catálogo parece capaz. Se ela nasce sem ferramenta nenhuma, sem uma capability que o trabalho
/// exige, ou em quarentena por desempenho, delegar-lhe um card é pior do que não ter persona — o
/// card falha depois de gastar cota, e o sistema aparenta capacidade que não tem.
///
/// Esta política é o portão entre as duas coisas. É PURA e conservadora: o que não puder ser
/// afirmado com o dado disponível vira inelegibilidade com motivo, nunca um "provavelmente serve".
/// </summary>
public static class PersonaEligibilityPolicy
{
    /// <summary>
    /// Estágios em que a persona existe para AUDITORIA, não para trabalho. Quarentena e desativação
    /// são decisões operacionais registradas — respeitá-las aqui é o que as torna reais.
    /// </summary>
    private static readonly HashSet<string> NonExecutableStates =
        new(["quarantined", "disabled"], StringComparer.OrdinalIgnoreCase);

    private static readonly string[] RiskOrder = ["low", "medium", "high", "critical"];

    public static PersonaEligibilityVerdict Evaluate(
        PersonaEligibilityInput persona,
        string projectId,
        string cardRiskTier,
        bool requiresTools)
    {
        ArgumentNullException.ThrowIfNull(persona);

        if (!persona.Enabled || persona.Archived)
        {
            return new PersonaEligibilityVerdict(false, "persona.disabled");
        }

        if (NonExecutableStates.Contains(persona.LifecycleState ?? string.Empty))
        {
            return new PersonaEligibilityVerdict(false, "persona.not_executable_lifecycle");
        }

        // Persona nascida para um projeto não atravessa para outro sem promoção auditada. Deixar
        // isso passar seria vazar equipe entre projetos por conveniência.
        if (persona.ScopeProjectId is { Length: > 0 } scope &&
            !string.Equals(scope, projectId, StringComparison.Ordinal))
        {
            return new PersonaEligibilityVerdict(false, "persona.out_of_project_scope");
        }

        // Capability OBRIGATÓRIA removida pela policy: a persona fica no catálogo para auditoria,
        // mas o card NÃO pode ser delegado a ela. Cair num agente incapaz em silêncio é o que
        // transforma "reduzir em vez de recusar" num fallback perigoso.
        if (persona.MissingRequiredCapabilities.Count > 0)
        {
            return new PersonaEligibilityVerdict(false, "persona.missing_required_capability");
        }

        // Persona sem ferramenta alguma pode existir; executar trabalho que exige ferramenta, não.
        if (requiresTools && persona.ToolIds.Count == 0)
        {
            return new PersonaEligibilityVerdict(false, "persona.no_tools_resolved");
        }

        // O risco do card não pode exceder o que a persona foi autorizada a assumir. Menor
        // privilégio vale também para o tipo de trabalho.
        //
        // INC-EVAL-002: o CHECK de agent_definitions.risk só admite low/medium/high — nenhuma
        // persona pode declarar teto 'critical' (migração 0043). Comparar cru contra o card fazia
        // todo card risk_tier='critical' ficar estruturalmente sem persona elegível, sempre. O teto
        // de comparação é fixado no MAIOR risco que uma persona pode declarar: um card crítico cabe
        // na persona de teto 'high' (a mais alta possível), mas os gates de prova de risco crítico
        // — revisão pareada obrigatória, evidência extra — continuam lendo o RiskTier real do card,
        // não este clamp, que é só o portão de elegibilidade.
        if (Math.Min(RankOf(cardRiskTier), MaxPersonaDeclarableRiskRank) > RankOf(persona.Risk))
        {
            return new PersonaEligibilityVerdict(false, "persona.risk_tier_exceeded");
        }

        return new PersonaEligibilityVerdict(true, "persona.eligible");
    }

    private static readonly int MaxPersonaDeclarableRiskRank = Array.IndexOf(RiskOrder, "high");

    /// <summary>
    /// Risco ausente conta como o MENOR: uma persona que nunca declarou faixa não herda
    /// autorização para trabalho crítico por omissão.
    /// </summary>
    private static int RankOf(string? risk)
    {
        var index = Array.FindIndex(
            RiskOrder,
            entry => string.Equals(
                entry, (risk ?? string.Empty).Trim().ToLower(CultureInfo.InvariantCulture),
                StringComparison.Ordinal));
        return index < 0 ? 0 : index;
    }
}
