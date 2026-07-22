using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Governance.Coordination;

namespace Harness.Host.Agents;

/// <summary>
/// Resolve QUAL profissional (persona) e QUAL papel de escopo uma demanda (card) exige — a
/// heurística INICIAL de planejamento do chefe. É deliberadamente simples e melhorável (a
/// fase de excelência refina isto): mapeia palavras da demanda para a persona especializada do
/// catálogo e para o papel de escopo (frontend/backend). Um card que já declare a persona
/// prevalece sobre a heurística.
/// </summary>
public static class ChiefCardResolver
{
    // Personas semeadas no catálogo (agent_key). A demanda vai ao profissional certo.
    public const string Engineer = "software-engineer";
    public const string Architect = "software-architect";
    public const string TechnicalWriter = "technical-writer";
    public const string ProductAnalyst = "product-requirements-analyst";
    public const string CriticQa = "critic-qa";

    public static ChiefCardResolution Resolve(
        string title, string instructionBody, IReadOnlyList<string> acceptanceCriteria,
        string riskTier, string? explicitPersonaKey = null, string? explicitRole = null)
    {
        var text = $"{title}\n{instructionBody}".ToLowerInvariant();

        var role = explicitRole ?? InferRole(text);
        var persona = explicitPersonaKey ?? InferPersona(text, role);
        var claims = AgentRoles.PathScopesFor(role);
        var capability = string.Equals(role, AgentRoles.Critic, StringComparison.OrdinalIgnoreCase)
            ? "review"
            : "code";

        var card = new DelegationCard(
            title,
            instructionBody,
            acceptanceCriteria,
            claims,
            riskTier);

        return new ChiefCardResolution(role, capability, persona, claims, card);
    }

    private static string InferRole(string text) =>
        MentionsAny(text, "frontend", "front-end", "ui", "ux", "componente", "tela", "css", "react")
            ? AgentRoles.FrontendSpecialist
            : AgentRoles.BackendSpecialist;

    private static string InferPersona(string text, string role)
    {
        if (MentionsAny(text, "arquitetura", "architecture", "adr", "fronteira", "boundary", "decisão técnica"))
        {
            return Architect;
        }

        if (MentionsAny(text, "document", "documenta", "readme", "guia", "manual", "changelog"))
        {
            return TechnicalWriter;
        }

        if (MentionsAny(text, "requisito", "requirement", "critério de aceite", "escopo do produto", "user story"))
        {
            return ProductAnalyst;
        }

        // Padrão: o engenheiro de software implementa a fatia (frontend ou backend).
        return Engineer;
    }

    private static bool MentionsAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Resolução de um card: o papel de escopo, a capacidade exigida, a persona especializada
/// (agent_key do catálogo), os claims autorizados e a demanda pronta para compor o briefing.
/// </summary>
public sealed record ChiefCardResolution(
    string Role,
    string RequiredCapability,
    string PersonaKey,
    IReadOnlyList<string> ScopeClaims,
    DelegationCard Card);
