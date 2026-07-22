using System.Text;
using Harness.Persistence.Abstractions.Agents;

namespace Harness.Host.Agents;

/// <summary>
/// A DEMANDA delegada (o card) — separada da persona. O card diz O QUE fazer (título, escopo,
/// critérios de aceite, paths autorizados, risco); QUEM faz e COMO vem da persona do agente
/// especializado, do catálogo. É o card que o operador audita e ajusta no board.
/// </summary>
public sealed record DelegationCard(
    string Title,
    string Scope,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> ScopeClaims,
    string RiskTier);

/// <summary>
/// Compõe o briefing entregue ao agente = PERSONA (do catálogo: quem/como) + CARD (a demanda:
/// o quê/critérios). Os dois são pontos de ajuste distintos e auditáveis: a persona molda o
/// comportamento do profissional (reusável entre demandas); o card molda a demanda específica.
/// Como uma empresa: a demanda vai ao profissional certo, não a um generalista.
///
/// É puro e determinístico — o mesmo par (persona, card) sempre rende o mesmo briefing, para
/// que o operador possa comparar "card X → resultado Y" e iterar o modelo.
/// </summary>
public static class PersonaCardComposer
{
    public static string Compose(AgentDefinitionContent persona, DelegationCard card)
    {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(card);

        var builder = new StringBuilder();

        // --- QUEM: a persona do profissional especializado (do catálogo) ---
        builder.Append("# Você é: ").Append(persona.Name.Trim());
        if (!string.IsNullOrWhiteSpace(persona.Specialty))
        {
            builder.Append(" — ").Append(persona.Specialty!.Trim());
        }

        builder.AppendLine().AppendLine();
        AppendParagraph(builder, persona.Persona ?? persona.Description);
        AppendSection(builder, "Sua missão", persona.Mission);
        AppendList(builder, "Princípios de operação", persona.OperatingPrinciples);
        AppendList(builder, "Entregáveis do seu papel", persona.Deliverables);
        AppendList(builder, "Critérios de qualidade do seu papel", persona.QualityCriteria);
        AppendSection(builder, "Estilo de comunicação", persona.CommunicationStyle);
        AppendList(builder, "Limitações do seu papel", persona.Limitations);

        // --- O QUÊ: a demanda específica (o card) ---
        builder.AppendLine("---").AppendLine();
        builder.Append("# Demanda: ").AppendLine(card.Title.Trim()).AppendLine();
        AppendSection(builder, "Escopo do trabalho", card.Scope);
        AppendList(builder, "Paths autorizados (só estes)", card.ScopeClaims);
        AppendList(builder, "Critérios de aceite desta demanda", card.AcceptanceCriteria);
        if (!string.IsNullOrWhiteSpace(card.RiskTier))
        {
            builder.Append("Risco: ").AppendLine(card.RiskTier.Trim());
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendParagraph(StringBuilder builder, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            builder.AppendLine(text.Trim()).AppendLine();
        }
    }

    private static void AppendSection(StringBuilder builder, string heading, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            builder.Append("## ").AppendLine(heading);
            builder.AppendLine(text.Trim()).AppendLine();
        }
    }

    private static void AppendList(StringBuilder builder, string heading, IReadOnlyList<string>? items)
    {
        var present = (items ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
        if (present.Length == 0)
        {
            return;
        }

        builder.Append("## ").AppendLine(heading);
        foreach (var item in present)
        {
            builder.Append("- ").AppendLine(item.Trim());
        }

        builder.AppendLine();
    }
}
