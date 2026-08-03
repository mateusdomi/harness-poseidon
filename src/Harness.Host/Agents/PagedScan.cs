namespace Harness.Host.Agents;

/// <summary>
/// Percorre uma consulta paginada até o fim.
///
/// Existe porque uma primeira página lida sozinha é um limite invisível: a reconciliação de
/// tentativas mortas lia 50 cards e, num projeto com mais de cinquenta cards ativos ao mesmo
/// tempo, os excedentes nunca eram olhados. Card preso em `running` para sempre, e nada no log
/// dizendo que ele sequer entrou na varredura — a pior forma de falhar, porque parece silêncio
/// de quem não tem o que fazer.
///
/// O teto de páginas não é uma escolha de desempenho: é para que um contador de total errado
/// não transforme a varredura em laço infinito dentro do ciclo do Chefe.
/// </summary>
internal static class PagedScan
{
    internal const int DefaultPageSize = 50;
    internal const int DefaultMaximumPages = 40;

    /// <param name="fetch">Busca uma página a partir de um deslocamento: devolve os itens e o total.</param>
    internal static async Task<(IReadOnlyList<T> Items, int Total)> CollectAsync<T>(
        Func<int, int, Task<(IReadOnlyList<T> Items, int Total)>> fetch,
        CancellationToken token,
        int pageSize = DefaultPageSize,
        int maximumPages = DefaultMaximumPages)
    {
        ArgumentNullException.ThrowIfNull(fetch);

        var items = new List<T>();
        var total = 0;

        for (var pageIndex = 0; pageIndex < maximumPages; pageIndex++)
        {
            token.ThrowIfCancellationRequested();
            var (pageItems, pageTotal) = await fetch(pageIndex * pageSize, pageSize);
            total = pageTotal;
            items.AddRange(pageItems);

            // Página curta encerra: é o sinal confiável de fim, mesmo quando o total mente.
            if (pageItems.Count < pageSize || items.Count >= pageTotal)
            {
                break;
            }
        }

        return (items, total);
    }
}
