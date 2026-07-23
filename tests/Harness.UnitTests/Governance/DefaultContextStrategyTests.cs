using Harness.Modules.Governance.Context;

namespace Harness.UnitTests.Governance;

/// <summary>
/// PLAT-02: regras da estratégia de contexto do Chief. A política é pura e determinística (sem IO,
/// sem relógio/aleatório interno): NO-OP sob o limiar, compactação por limiar preservando
/// fixados/críticos e recentes, limpeza de tool-result fora da janela, note-taking externalizado e
/// idempotência.
/// </summary>
public sealed class DefaultContextStrategyTests
{
    private static readonly DefaultContextStrategy Strategy = new();

    private static ContextItem Message(string id, long seq, int tokens, string content,
        bool pinned = false, bool critical = false) =>
        new(id, "user", ContextItemKind.Message, content, tokens, seq, Pinned: pinned, Critical: critical);

    private static ContextItem Tool(string id, long seq, int tokens, string content) =>
        new(id, "tool", ContextItemKind.ToolResult, content, tokens, seq);

    [Fact]
    public void UnderThresholdIsANoOp()
    {
        var items = new[]
        {
            Message("a", 0, 30, "hello"),
            Message("b", 1, 30, "world"),
        };

        var result = Strategy.Apply(items, new ContextStrategyBudget(100, 2, 1));

        Assert.False(result.Compacted);
        Assert.Empty(result.Notes);
        Assert.Equal(60, result.EstimatedTokens);
        Assert.Equal(items, result.Context);
    }

    [Fact]
    public void OverThresholdCompactsPreservingPinnedCriticalAndRecentAndDropsOldest()
    {
        var items = new[]
        {
            Message("i0", 0, 50, "founding", critical: true),
            Message("i1", 1, 50, "old chatter"),
            Tool("i2", 2, 50, "big tool A"),
            Tool("i3", 3, 50, "big tool B"),
            Message("i4", 4, 50, "recent-1"),
            Message("i5", 5, 50, "recent-2"),
        };

        var result = Strategy.Apply(items, new ContextStrategyBudget(100, 2, 1));

        Assert.True(result.Compacted);

        // Oldest low-value message (not pinned/critical/recent) is dropped.
        Assert.DoesNotContain(result.Context, item => item.Id == "i1");

        // Critical item preserved verbatim (content intact) and marked noted.
        var founding = Assert.Single(result.Context, item => item.Id == "i0");
        Assert.Equal("founding", founding.Content);
        Assert.True(founding.Noted);

        // The two most recent turns are preserved verbatim.
        Assert.Contains(result.Context, item => item.Id == "i4" && item.Content == "recent-1");
        Assert.Contains(result.Context, item => item.Id == "i5" && item.Content == "recent-2");
    }

    [Fact]
    public void ToolResultsBeyondTheWindowAreCleared()
    {
        var items = new[]
        {
            Message("i0", 0, 50, "founding", critical: true),
            Tool("i2", 2, 50, "big tool A"),
            Tool("i3", 3, 50, "big tool B"),
            Message("i4", 4, 50, "recent-1"),
            Message("i5", 5, 50, "recent-2"),
        };

        var result = Strategy.Apply(items, new ContextStrategyBudget(100, 2, 1));

        // i2 is beyond the tool-result window (window keeps only the latest tool-result i3).
        var cleared = Assert.Single(result.Context, item => item.Id == "i2");
        Assert.True(cleared.Cleared);
        Assert.StartsWith(DefaultContextStrategy.ClearedReferencePrefix, cleared.Content);
        Assert.NotEqual("big tool A", cleared.Content);
        Assert.True(cleared.EstimatedTokens < 50);

        // i3 is within the window and kept verbatim.
        var kept = Assert.Single(result.Context, item => item.Id == "i3");
        Assert.False(kept.Cleared);
        Assert.Equal("big tool B", kept.Content);
    }

    [Fact]
    public void CriticalItemsEmitDurableNotes()
    {
        var items = new[]
        {
            Message("i0", 0, 50, "the founding decision", critical: true),
            Message("i1", 1, 50, "old chatter"),
            Message("i2", 2, 50, "more chatter"),
            Message("i3", 3, 50, "recent"),
        };

        var result = Strategy.Apply(items, new ContextStrategyBudget(100, 1, 1));

        var note = Assert.Single(result.Notes);
        Assert.Equal("i0", note.SourceItemId);
        Assert.Equal("the founding decision", note.Content);
        Assert.Equal("user", note.Role);
    }

    [Fact]
    public void PinnedItemsAreNeverDroppedEvenWhenOld()
    {
        var items = new[]
        {
            Message("pin", 0, 50, "pinned constraint", pinned: true),
            Message("i1", 1, 50, "old chatter"),
            Message("i2", 2, 50, "more chatter"),
            Message("i3", 3, 50, "recent"),
        };

        var result = Strategy.Apply(items, new ContextStrategyBudget(100, 1, 1));

        Assert.True(result.Compacted);
        Assert.Contains(result.Context, item => item.Id == "pin" && item.Content == "pinned constraint");
        Assert.DoesNotContain(result.Context, item => item.Id == "i1");
        // Pinned but not critical: no note emitted.
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void CompactionIsDeterministicAndIdempotent()
    {
        var items = new[]
        {
            Message("i0", 0, 50, "founding", critical: true),
            Message("i1", 1, 50, "old chatter"),
            Tool("i2", 2, 50, "big tool A"),
            Tool("i3", 3, 50, "big tool B"),
            Message("i4", 4, 50, "recent-1"),
            Message("i5", 5, 50, "recent-2"),
        };
        var budget = new ContextStrategyBudget(100, 2, 1);

        var first = Strategy.Apply(items, budget);
        var second = Strategy.Apply(first.Context, budget);

        // A second application is a fixed point: identical context and no new notes.
        Assert.Equal(first.Context, second.Context);
        Assert.Empty(second.Notes);

        // Determinism: repeating the same input yields the same output.
        var repeat = Strategy.Apply(items, budget);
        Assert.Equal(first.Context, repeat.Context);
        Assert.Equal(first.Notes, repeat.Notes);
    }
}
