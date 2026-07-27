using Harness.Modules.Workflows.Application;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Abstractions.Workflows;

namespace Harness.Host.Workflows;

/// <summary>
/// Deriva as OBRIGAÇÕES REAIS de uma fase a partir do que a fase exige e do trabalho que existe
/// no board para ela. É puro e determinístico: as mesmas entradas rendem sempre o mesmo plano.
///
/// O defeito que este planejador corrige é de premissa, não de código: a esteira media a fase
/// pelos documentos previstos. Em Triagem ou Descoberta isso coincide — o documento É o
/// entregável. Em Desenvolvimento, Testes, Homologação e Release, não: produzir "briefing
/// técnico", "code review estruturado", "métricas DORA" e "dicionário ubíquo" fecharia a fase sem
/// nenhuma linha implementada. Aqui, cada card de trabalho da fase vira uma obrigação com o mesmo
/// peso de um documento, e o documento passa a concluir apenas a si mesmo.
/// </summary>
public static class PhaseObligationPlanner
{
    /// <summary>Prefixo estável da obrigação de um objetivo-documento da fase.</summary>
    public const string DocumentPrefix = "document:";

    /// <summary>Prefixo estável da obrigação de um card de trabalho da fase.</summary>
    public const string CardPrefix = "card:";

    /// <summary>Tipos de card que representam CONSTRUÇÃO, não cerimônia.</summary>
    private static readonly HashSet<string> ImplementationCardTypes =
        new(["agent_task", "feature", "bug"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Monta o plano da fase. <paramref name="phaseCards"/> são os cards já ligados à fase (o
    /// trabalho que o dono pediu e que a chefe decompôs); <paramref name="documentObjectives"/>
    /// são os objetivos-documento que a definição da fase exige.
    ///
    /// Os pesos são NORMALIZADOS entre as obrigações obrigatórias — sem constantes espalhadas pelo
    /// código e sem privilegiar documento sobre implementação, que é exatamente o viés que fazia a
    /// fase parecer pronta sem trabalho feito.
    /// </summary>
    public static IReadOnlyList<PhaseObligationInput> Plan(
        IReadOnlyList<(string ObjectiveKey, string Name)> documentObjectives,
        IReadOnlyList<BoardTaskRecord> phaseCards)
    {
        ArgumentNullException.ThrowIfNull(documentObjectives);
        ArgumentNullException.ThrowIfNull(phaseCards);

        var drafts = new List<PhaseObligationInput>();

        foreach (var (objectiveKey, name) in documentObjectives)
        {
            drafts.Add(new PhaseObligationInput(
                $"{DocumentPrefix}{objectiveKey}",
                PhaseProgressEvaluator.ToStorage(PhaseObligationKind.Document),
                $"Produzir e aceitar o artefato \"{name}\".",
                Required: true,
                Weight: 1,
                Source: "template",
                CompletionCriteria:
                    "O artefato existe com conteúdo real, passou por revisão independente e foi aceito.",
                ObjectiveKey: objectiveKey));
        }

        foreach (var card in phaseCards)
        {
            // O gate humano de integração e a decisão não são trabalho a produzir: são pontos de
            // controle sobre o trabalho. Contá-los como obrigação faria o denominador crescer com
            // cerimônia e o percentual medir o próprio rito.
            if (string.Equals(card.CardType, "human_gate", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(card.CardType, "decision", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var kind = ImplementationCardTypes.Contains(card.CardType ?? string.Empty)
                ? PhaseObligationKind.Implementation
                : PhaseObligationKind.Operation;
            drafts.Add(new PhaseObligationInput(
                $"{CardPrefix}{card.Id}",
                PhaseProgressEvaluator.ToStorage(kind),
                card.Title,
                Required: true,
                Weight: 1,
                Source: "plan",
                CompletionCriteria:
                    "O card está concluído com revisão independente aprovada e evidências anexadas.",
                CardId: card.Id));
        }

        // Peso igual entre as obrigatórias do mesmo plano. Um peso vindo do plano prevaleceria;
        // enquanto o template não declara peso, igualdade é a única distribuição defensável — e é
        // auditável, que é o que a alternativa (constantes espalhadas) nunca foi.
        var requiredCount = drafts.Count(draft => draft.Required);
        var weight = (double)PhaseProgressEvaluator.DefaultWeight(requiredCount);
        return [.. drafts.Select(draft => draft.Required ? draft with { Weight = weight } : draft)];
    }

    /// <summary>
    /// Verdadeiro quando a fase declara artefatos obrigatórios mas NENHUM card do board os
    /// produz — a inconsistência que deixava o portão esperando para sempre por trabalho que
    /// ninguém ia fazer.
    /// </summary>
    public static bool HasArtifactWithoutProducer(
        IReadOnlyList<PhaseObligationRecord> obligations) =>
        obligations.Any(obligation =>
            obligation.Required &&
            string.Equals(obligation.State, "pending", StringComparison.Ordinal) &&
            obligation.CardId is null &&
            obligation.ObjectiveKey is null);
}
