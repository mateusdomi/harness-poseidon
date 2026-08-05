namespace Harness.Modules.Workflows.Product;

/// <summary>
/// A janela oficial de avaliação empresarial — a linha que separa "preparação da plataforma" de
/// "desenvolvimento real sob avaliação". Nada é apagado da história: os testes e preflights
/// continuam no arquivo; eles apenas ficam FORA deste recorte. Todo relatório do dia da
/// demonstração filtra por <see cref="Includes"/>: timestamp dentro da janela E projeto
/// pertencente à avaliação. Determinístico, sem estado, impossível de "esquecer" o filtro.
/// </summary>
public sealed record EvaluationWindow(
    string Name,
    DateTimeOffset Start,
    DateTimeOffset Deadline,
    IReadOnlyList<string> ProjectIds)
{
    public bool Includes(DateTimeOffset timestamp, string projectId) =>
        timestamp >= Start &&
        ProjectIds.Contains(projectId, StringComparer.Ordinal);

    /// <summary>Recorta qualquer coleção derivável do ledger para o universo da avaliação.</summary>
    public IReadOnlyList<T> Filter<T>(
        IEnumerable<T> items,
        Func<T, DateTimeOffset> timestampOf,
        Func<T, string> projectOf)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(timestampOf);
        ArgumentNullException.ThrowIfNull(projectOf);
        return [.. items.Where(item => Includes(timestampOf(item), projectOf(item)))];
    }
}
