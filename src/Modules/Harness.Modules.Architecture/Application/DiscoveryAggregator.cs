using Harness.Modules.Architecture.Contracts;
using Harness.Modules.Architecture.Domain;

namespace Harness.Modules.Architecture.Application;

/// <summary>
/// ARC-06 — agregação PURA e determinística das descobertas de sistemas existentes. Agrupa por sujeito
/// (o sistema descoberto), faz o rollup da CONFIANÇA de forma CONSERVADORA (o elo mais fraco entre as
/// informações ainda abertas), agrega as PERGUNTAS PENDENTES e conta as fontes. Nada é inventado: cada
/// número deriva de uma descoberta gravada, cada uma com confiança + evidência + perguntas explícitas.
/// </summary>
public static class DiscoveryAggregator
{
    /// <summary>Um fato de descoberta puro, já materializado do store (sem IO).</summary>
    public sealed record DiscoveryFact(
        string? SystemId,
        string SubjectName,
        string SourceKind,
        string Confidence,
        string Status,
        IReadOnlyList<string> PendingQuestions);

    public static DiscoverySummaryListContract Summarize(IReadOnlyList<DiscoveryFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // Agrupa por sujeito: preferimos o systemId quando presente; senão, o nome do candidato.
        var groups = facts
            .GroupBy(f => f.SystemId ?? "name:" + f.SubjectName, StringComparer.Ordinal)
            .Select(BuildSummary)
            .OrderByDescending(s => s.OpenCount)
            .ThenBy(s => s.SubjectName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DiscoverySummaryListContract(groups.Length, groups);
    }

    private static DiscoverySystemSummaryContract BuildSummary(IEnumerable<DiscoveryFact> raw)
    {
        var items = raw.ToArray();
        var first = items[0];

        var open = items.Where(i => i.Status == ArchitectureHubKinds.StatusOpen).ToArray();
        var confirmed = items.Count(i => i.Status == "confirmed");

        // Confiança geral: o MENOR nível entre as descobertas ainda abertas (o elo mais fraco). Quando
        // não há nada aberto, refletimos a menor confiança entre as confirmadas; se vazio, "high".
        var pool = open.Length > 0
            ? open
            : items.Where(i => i.Status == "confirmed").ToArray();
        var overall = pool.Length == 0
            ? "high"
            : pool
                .Select(i => i.Confidence)
                .OrderBy(ArchitectureHubKinds.ConfidenceRank)
                .First();

        var bySource = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            bySource[item.SourceKind] = bySource.GetValueOrDefault(item.SourceKind) + 1;
        }

        // Perguntas pendentes só das descobertas ainda abertas, sem duplicatas, ordem estável.
        var questions = open
            .SelectMany(i => i.PendingQuestions)
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(q => q, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DiscoverySystemSummaryContract(
            first.SystemId,
            first.SubjectName,
            items.Length,
            open.Length,
            confirmed,
            overall,
            bySource,
            questions);
    }
}
