using Harness.Modules.Governance.Documentation;

namespace Harness.Modules.Governance.Context;

/// <summary>Natureza de um item da janela de contexto do Chief.</summary>
public enum ContextItemKind
{
    /// <summary>Mensagem de conversa (usuário/assistente/sistema).</summary>
    Message,

    /// <summary>Resultado bruto de ferramenta — verboso e de baixo valor fora da recência.</summary>
    ToolResult,

    /// <summary>Nota durável já externalizada e reinjetada no contexto.</summary>
    Note,
}

/// <summary>
/// Item ordenado da janela de trabalho do Chief. Puro dado: <see cref="Sequence"/> ordena
/// (maior = mais recente), <see cref="EstimatedTokens"/> é o custo estimado já calculado pelo
/// chamador, <see cref="Pinned"/>/<see cref="Critical"/> marcam preservação, e
/// <see cref="Cleared"/>/<see cref="Noted"/> são estados idempotentes gravados pela estratégia.
/// </summary>
public sealed record ContextItem(
    string Id,
    string Role,
    ContextItemKind Kind,
    string Content,
    int EstimatedTokens,
    long Sequence,
    bool Pinned = false,
    bool Critical = false,
    bool Cleared = false,
    bool Noted = false);

/// <summary>
/// Orçamento/limiar da estratégia de contexto. <see cref="MaxTokens"/> é o limiar acima do qual a
/// compactação dispara; <see cref="RecentTurns"/> é a quantidade de itens mais recentes sempre
/// preservada verbatim; <see cref="ToolResultWindow"/> é a janela de resultados de ferramenta
/// mantidos verbatim (os demais viram referência compacta).
/// </summary>
public sealed record ContextStrategyBudget(int MaxTokens, int RecentTurns, int ToolResultWindow)
{
    public static ContextStrategyBudget Default { get; } = new(12000, 12, 4);

    public void Validate()
    {
        if (MaxTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTokens), "The context threshold must be at least one token.");
        }

        if (RecentTurns < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RecentTurns), "The recent-turn window cannot be negative.");
        }

        if (ToolResultWindow < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ToolResultWindow), "The tool-result window cannot be negative.");
        }
    }
}

/// <summary>
/// Fato durável a persistir fora da compactação. A estratégia apenas EMITE a nota; a persistência
/// é responsabilidade do chamador (a memória vive no store, não na janela compactada).
/// </summary>
public sealed record ContextNote(
    string SourceItemId,
    string Role,
    string Content,
    int EstimatedTokens,
    long Sequence);

/// <summary>
/// Resultado puro da estratégia: a janela de contexto transformada, as notas a persistir, o total
/// estimado de tokens da janela transformada e se houve compactação.
/// </summary>
public sealed record ContextStrategyResult(
    IReadOnlyList<ContextItem> Context,
    IReadOnlyList<ContextNote> Notes,
    int EstimatedTokens,
    bool Compacted);

/// <summary>
/// Política de contexto do Chief: mantém a janela de trabalho limitada ao longo de um projeto longo.
/// PURA e trocável por deployment (registrada em DI atrás desta interface).
/// </summary>
public interface IContextStrategy
{
    ContextStrategyResult Apply(IReadOnlyList<ContextItem> context, ContextStrategyBudget budget);
}

/// <summary>
/// Estratégia de contexto padrão, determinística e sem IO (nenhum relógio/aleatório interno; o
/// 'now'/orçamento entram por parâmetro). Espelha a forma dos avaliadores puros existentes
/// (<c>ReadinessEvaluator</c>, <c>CardReadinessEvaluator</c>). Regras:
/// <list type="bullet">
/// <item>NO-OP sob o limiar: se os tokens estimados ≤ <see cref="ContextStrategyBudget.MaxTokens"/>,
/// devolve a janela intacta e nenhuma nota — o comportamento do Chief não regride.</item>
/// <item>COMPACTAÇÃO por limiar: acima do limiar, os itens antigos de baixo valor (não fixados, não
/// críticos, fora da recência) são descartados, preservando SEMPRE os fixados/críticos e os turnos
/// mais recentes.</item>
/// <item>LIMPEZA de tool-result: resultados de ferramenta fora da janela de recência viram uma
/// referência compacta (não são mantidos verbatim).</item>
/// <item>NOTE-TAKING externalizado: cada item crítico emite uma nota durável exatamente uma vez
/// (marcado <see cref="ContextItem.Noted"/> no resultado), tornando a operação idempotente.</item>
/// </list>
/// </summary>
public sealed class DefaultContextStrategy : IContextStrategy
{
    public const string ClearedReferencePrefix = "[tool-result cleared:";

    public ContextStrategyResult Apply(IReadOnlyList<ContextItem> context, ContextStrategyBudget budget)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(budget);
        budget.Validate();

        var ordered = context
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var total = ordered.Sum(item => item.EstimatedTokens);

        // NO-OP sob o limiar: janela idêntica, sem notas, sem mutação — sem regressão.
        if (total <= budget.MaxTokens)
        {
            return new ContextStrategyResult(ordered, [], total, false);
        }

        var recentIds = ordered
            .OrderByDescending(item => item.Sequence)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .Take(budget.RecentTurns)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);

        var recentToolResultIds = ordered
            .Where(item => item.Kind == ContextItemKind.ToolResult)
            .OrderByDescending(item => item.Sequence)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .Take(budget.ToolResultWindow)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);

        var kept = new List<ContextItem>(ordered.Length);
        var notes = new List<ContextNote>();

        foreach (var item in ordered)
        {
            var emitNote = item.Critical && !item.Noted;
            if (emitNote)
            {
                notes.Add(new ContextNote(item.Id, item.Role, item.Content, item.EstimatedTokens, item.Sequence));
            }

            // Itens fixados, críticos ou recentes são preservados verbatim. Os críticos são marcados
            // como 'Noted' para que uma reaplicação não reemita a mesma nota (idempotência).
            if (item.Pinned || item.Critical || recentIds.Contains(item.Id))
            {
                kept.Add(emitNote ? item with { Noted = true } : item);
                continue;
            }

            // Região antiga: resultados de ferramenta fora da janela viram referência compacta;
            // qualquer outro item de baixo valor é descartado (compactado).
            if (item.Kind == ContextItemKind.ToolResult)
            {
                if (recentToolResultIds.Contains(item.Id) || item.Cleared)
                {
                    kept.Add(item);
                    continue;
                }

                var reference = $"{ClearedReferencePrefix} {item.Id}]";
                kept.Add(item with
                {
                    Content = reference,
                    EstimatedTokens = GovernanceManifestSynchronizer.EstimateTokens(reference),
                    Cleared = true,
                });
            }
        }

        return new ContextStrategyResult(kept, notes, kept.Sum(item => item.EstimatedTokens), true);
    }
}
