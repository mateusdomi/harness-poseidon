using Harness.Host.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// Uma primeira página lida sozinha é um limite invisível. A reconciliação de tentativas
/// mortas varria 50 cards: num projeto com mais de cinquenta cards ativos ao mesmo tempo, os
/// excedentes nunca eram olhados, ficavam presos em `running` para sempre, e nada no log dizia
/// que eles sequer entraram na varredura.
/// </summary>
public sealed class PagedScanTests
{
    private static Func<int, int, Task<(IReadOnlyList<int> Items, int Total)>> Source(
        int total, List<int>? offsets = null) =>
        (offset, size) =>
        {
            offsets?.Add(offset);
            var items = Enumerable.Range(offset, Math.Max(0, Math.Min(size, total - offset))).ToArray();
            return Task.FromResult<(IReadOnlyList<int>, int)>((items, total));
        };

    [Fact]
    public async Task EverythingBeyondTheFirstPageIsStillScanned()
    {
        var offsets = new List<int>();

        var (items, total) = await PagedScan.CollectAsync<int>(
            Source(127, offsets), CancellationToken.None);

        Assert.Equal(127, total);
        Assert.Equal(127, items.Count);
        Assert.Equal([0, 50, 100], offsets);
    }

    [Fact]
    public async Task ASingleShortPageStopsImmediately()
    {
        var offsets = new List<int>();

        var (items, _) = await PagedScan.CollectAsync<int>(Source(7, offsets), CancellationToken.None);

        Assert.Equal(7, items.Count);
        Assert.Equal([0], offsets);
    }

    /// <summary>
    /// Um total mentiroso (maior que o que a fonte devolve) não pode virar laço infinito dentro
    /// do ciclo do Chefe: a página curta encerra a varredura.
    /// </summary>
    [Fact]
    public async Task ALyingTotalDoesNotSpinForever()
    {
        var calls = 0;

        var (items, _) = await PagedScan.CollectAsync<int>(
            (offset, size) =>
            {
                calls++;
                return Task.FromResult<(IReadOnlyList<int>, int)>((offset == 0 ? [.. Enumerable.Range(0, size)] : [], 10_000));
            },
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(PagedScan.DefaultPageSize, items.Count);
    }

    /// <summary>O teto de páginas é o cinto de segurança: uma fonte que nunca encurta para.</summary>
    [Fact]
    public async Task AnEndlessSourceStopsAtThePageCeiling()
    {
        var calls = 0;

        _ = await PagedScan.CollectAsync<int>(
            (offset, size) =>
            {
                calls++;
                return Task.FromResult<(IReadOnlyList<int>, int)>(([.. Enumerable.Range(offset, size)], int.MaxValue));
            },
            CancellationToken.None);

        Assert.Equal(PagedScan.DefaultMaximumPages, calls);
    }
}
