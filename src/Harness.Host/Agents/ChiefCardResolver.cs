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
        // A heurística lê a DEMANDA, não os marcadores de despacho. A linha "Especialidade
        // exigida: architecture-security" carrega a palavra "architecture" — deixá-la no palheiro
        // faria a persona declarada enviesar o próprio fallback dela, e uma chave inválida
        // arrastaria a inferência junto.
        var text = $"{title}\n{WithoutSpecialtyMarker(instructionBody)}".ToLowerInvariant();

        // Um card que DECLARA o papel (o materializer de planos escreve "Papel exigido: <role>")
        // prevalece sobre qualquer heurística — a heurística só existe para cards escritos à mão.
        var role = explicitRole ?? ParseDeclaredRole(instructionBody) ?? InferRole(text);

        // A persona INFERIDA é sempre calculada, mesmo quando há uma declarada: ela é o fallback
        // usado quando a chave declarada não existe (ou não está habilitada) no catálogo. Sem esse
        // fallback, uma chave inválida escrita pelo modelo deixaria o card sem persona alguma e o
        // briefing degradaria para o texto cru do escopo — o executor perderia o "quem/como".
        var inferred = InferPersona(text, role);
        var persona = explicitPersonaKey ?? ParseDeclaredSpecialty(instructionBody) ?? inferred;
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

        return new ChiefCardResolution(role, capability, persona, claims, card, inferred);
    }

    /// <summary>
    /// Especialidade declarada no corpo da instrução ("Especialidade exigida: &lt;agent_key&gt;"),
    /// escrita pelo materializador quando o Chefe declarou quem é o profissional qualificado. Aqui
    /// só o formato é validado; a EXISTÊNCIA da persona é conferida contra o catálogo no despacho,
    /// porque autoridade é do sistema, nunca do texto que o modelo produziu.
    /// </summary>
    private const string SpecialtyMarker = "especialidade exigida:";

    private static string WithoutSpecialtyMarker(string instructionBody) =>
        string.Join(
            '\n',
            instructionBody.Split('\n').Where(line =>
                !line.Trim().StartsWith(SpecialtyMarker, StringComparison.OrdinalIgnoreCase)));

    private static string? ParseDeclaredSpecialty(string instructionBody)
    {
        const string marker = SpecialtyMarker;
        foreach (var line in instructionBody.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var declared = trimmed[marker.Length..].Trim();
            return declared.Length is > 0 and <= 100 ? declared : null;
        }

        return null;
    }

    /// <summary>
    /// Papel declarado no corpo da instrução ("Papel exigido: backend-specialist|frontend-specialist|critic").
    /// "none" e valores desconhecidos caem para a heurística (o card de documentação, por exemplo,
    /// declara "none" e é implementado pelo papel inferido do texto).
    /// </summary>
    private static string? ParseDeclaredRole(string instructionBody)
    {
        const string marker = "papel exigido:";
        foreach (var line in instructionBody.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var declared = trimmed[marker.Length..].Trim();
            if (string.Equals(declared, AgentRoles.BackendSpecialist, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(declared, AgentRoles.FrontendSpecialist, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(declared, AgentRoles.Critic, StringComparison.OrdinalIgnoreCase))
            {
                return declared.ToLowerInvariant();
            }

            return null;
        }

        return null;
    }

    // "ui"/"ux" como SUBSTRING viravam falso positivo em português ("concluir", "incluir",
    // "possui" contêm "ui") e mandavam card de backend para o papel de frontend. Os termos
    // curtos exigem fronteira de palavra; os longos continuam por substring.
    private static string InferRole(string text) =>
        MentionsAny(text, "frontend", "front-end", " ui ", "ui/", "/ui", " ux ", "componente", " tela", "css", "react")
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
    DelegationCard Card,

    /// <summary>
    /// A persona que a heurística escolheria. Igual a <see cref="PersonaKey"/> quando nada foi
    /// declarado; é o fallback quando a chave declarada não existe no catálogo.
    /// </summary>
    string InferredPersonaKey = "");
