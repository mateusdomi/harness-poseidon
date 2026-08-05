using System.Globalization;
using System.Text;

namespace Harness.Modules.Workflows.Product;

/// <summary>Os fatos brutos de um projeto, todos deriváveis do que o ledger já grava.</summary>
public sealed record DeliveryFacts(
    string ProjectName,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastActivityAt,
    int PhasesCompleted,
    int PhasesTotal,
    int Cards,
    int AgentAttempts,
    int ApprovedAttempts,
    int RejectedAttempts,
    int HumanMessages,
    int HumanTechnicalInterventions,
    int CorrectiveCardsAutoCreated,
    int RequirementsSatisfied,
    int RequirementsTotal,
    int CommitsMerged,
    int BlindRetriesPrevented);

/// <summary>
/// O relatório de evidência de produtividade — o número que a tese empresarial precisa, derivado
/// do que já está gravado, sem telemetria nova.
///
/// A régua honesta é uma só: <b>quanto do trabalho exigiu uma pessoa técnica?</b> O relatório
/// separa mensagem humana (conversar com a chefe é uso, não intervenção) de INTERVENÇÃO TÉCNICA
/// (criar card à mão, reverter decisão, corrigir configuração) — porque a promessa do acelerador
/// não é "ninguém fala com o sistema"; é "o desenvolvedor não passa o dia escrevendo prompt".
///
/// O que este relatório NUNCA faz: inventar multiplicador. "5x" sem medição de linha de base é
/// marketing; o que dá para afirmar é o que está aqui — tentativas de agente, aprovações, correções
/// automáticas, intervenções humanas — e deixar a comparação para quem conhece o próprio time.
/// </summary>
public static class ProductivityEvidence
{
    public static string Compose(DeliveryFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var builder = new StringBuilder();
        var elapsed = (facts.LastActivityAt ?? facts.StartedAt) - facts.StartedAt;

        builder.Append("# Relatório de evidência de entrega — ").Append(facts.ProjectName).Append("\n\n");
        builder.Append("| Métrica | Valor |\n|---|---|\n");
        Row(builder, "Tempo decorrido", elapsed.TotalHours.ToString("F1", CultureInfo.InvariantCulture) + " h");
        Row(builder, "Fases concluídas", $"{facts.PhasesCompleted}/{facts.PhasesTotal}");
        Row(builder, "Cards", facts.Cards.ToString(CultureInfo.InvariantCulture));
        Row(builder, "Execuções de agente", facts.AgentAttempts.ToString(CultureInfo.InvariantCulture));
        Row(builder, "Tentativas aprovadas", facts.ApprovedAttempts.ToString(CultureInfo.InvariantCulture));
        Row(builder, "Tentativas rejeitadas pela revisão independente", facts.RejectedAttempts.ToString(CultureInfo.InvariantCulture));
        Row(builder, "Cards corretivos criados AUTOMATICAMENTE", facts.CorrectiveCardsAutoCreated.ToString(CultureInfo.InvariantCulture));
        Row(builder, "Repetições cegas impedidas", facts.BlindRetriesPrevented.ToString(CultureInfo.InvariantCulture));
        Row(builder, "Requisitos satisfeitos", $"{facts.RequirementsSatisfied}/{facts.RequirementsTotal}");
        Row(builder, "Commits integrados por portão", facts.CommitsMerged.ToString(CultureInfo.InvariantCulture));
        Row(builder, "Mensagens humanas (uso)", facts.HumanMessages.ToString(CultureInfo.InvariantCulture));
        Row(builder, "**Intervenções técnicas humanas**", $"**{facts.HumanTechnicalInterventions}**");

        builder.Append('\n');
        if (facts.AgentAttempts > 0)
        {
            var automated = facts.AgentAttempts - facts.HumanTechnicalInterventions;
            builder.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"Das {facts.AgentAttempts} execuções, {Math.Max(0, automated)} correram sem "))
                .Append("intervenção técnica humana. ");
        }

        builder.Append(
            "Nenhum multiplicador de produtividade é declarado sem linha de base medida: os " +
            "números acima são deriváveis do ledger e auditáveis um a um.\n");
        return builder.ToString();
    }

    private static void Row(StringBuilder builder, string label, string value) =>
        builder.Append("| ").Append(label).Append(" | ").Append(value).Append(" |\n");
}
