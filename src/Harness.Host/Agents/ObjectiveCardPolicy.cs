namespace Harness.Host.Agents;

/// <summary>
/// Parâmetros de execução do CARD-OBJETIVO (perfil v2, Understand → Build → Prove).
///
/// Um card-objetivo é um objetivo funcional inteiro entregue por UM executor persistente — não
/// um micro-card. Os tetos de tempo, silêncio e contexto dos micro-cards (30min/15min/16k) foram
/// medidos na avaliação TrensRJ como parte do custo estrutural do caminho crítico; o perfil
/// objetivo os substitui pelos valores de sessão longa, num lugar só, decidido pelo CardType.
/// </summary>
public static class ObjectiveCardPolicy
{
    public const string CardType = "objetivo";

    public static bool IsObjective(string? cardType) =>
        string.Equals(cardType, CardType, StringComparison.Ordinal);

    public static TimeSpan RunTimeout(AgentRunSettings settings, string? cardType) =>
        IsObjective(cardType) ? settings.ObjectiveRunTimeout : settings.RunTimeout;

    public static TimeSpan RunNoProgressTimeout(AgentRunSettings settings, string? cardType) =>
        IsObjective(cardType) ? settings.ObjectiveRunNoProgressTimeout : settings.RunNoProgressTimeout;

    public static int ContextTokenBudget(AgentRunSettings settings, string? cardType) =>
        IsObjective(cardType) ? settings.ObjectiveContextTokenBudget : settings.ContextTokenBudget;
}
