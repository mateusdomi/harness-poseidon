using Harness.Host.Conversations;
using Harness.Persistence.Abstractions.Conversations;

namespace Harness.UnitTests.Conversations;

/// <summary>
/// Notification Router: com vários vínculos na MESMA conversa, apenas o último canal ativo
/// publica a saída. A eleição precisa ser determinística — dois processos decidem igual sem
/// coordenação — e não pode alterar o caso de vínculo único.
/// </summary>
public sealed class ActiveChannelRouterTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Conversation = "01ARZ3NDEKTSV4RRFFQ69G5FB1";
    private static readonly DateTimeOffset Linked = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SingleLinkOnTheConversationIsAlwaysActive()
    {
        var link = Link("01ARZ3NDEKTSV4RRFFQ69G5FC1", "telegram", lastInboundAt: null);
        var router = new ActiveChannelRouter(new FakeChannelLinkStore([link]));

        Assert.True(await router.IsActiveAsync(Tenant, link, CancellationToken.None));
    }

    [Fact]
    public async Task MostRecentInboundWinsAndTheOthersAreSilenced()
    {
        var telegram = Link("01ARZ3NDEKTSV4RRFFQ69G5FC1", "telegram", Linked.AddMinutes(5));
        var whatsapp = Link("01ARZ3NDEKTSV4RRFFQ69G5FC2", "whatsapp", Linked.AddMinutes(30));
        var email = Link("01ARZ3NDEKTSV4RRFFQ69G5FC3", "email", Linked.AddMinutes(10));
        var router = new ActiveChannelRouter(new FakeChannelLinkStore([telegram, whatsapp, email]));

        var active = await router.SelectActiveAsync(
            Tenant, Conversation, CancellationToken.None);
        Assert.Equal(whatsapp.Id, active!.Id);
        Assert.True(await router.IsActiveAsync(Tenant, whatsapp, CancellationToken.None));
        Assert.False(await router.IsActiveAsync(Tenant, telegram, CancellationToken.None));
        Assert.False(await router.IsActiveAsync(Tenant, email, CancellationToken.None));
    }

    [Fact]
    public async Task LinkWithInboundBeatsLinkThatNeverReceivedAnything()
    {
        var neverUsed = Link("01ARZ3NDEKTSV4RRFFQ69G5FC9", "teams", lastInboundAt: null);
        var used = Link("01ARZ3NDEKTSV4RRFFQ69G5FC1", "telegram", Linked.AddMinutes(1));
        var router = new ActiveChannelRouter(new FakeChannelLinkStore([neverUsed, used]));

        var active = await router.SelectActiveAsync(
            Tenant, Conversation, CancellationToken.None);
        Assert.Equal(used.Id, active!.Id);
    }

    [Fact]
    public async Task TiedInboundIsBrokenDeterministicallyByLinkedAtThenId()
    {
        var sameInstant = Linked.AddMinutes(7);
        var older = Link("01ARZ3NDEKTSV4RRFFQ69G5FC1", "telegram", sameInstant, Linked);
        var newer = Link("01ARZ3NDEKTSV4RRFFQ69G5FC2", "whatsapp", sameInstant, Linked.AddHours(1));
        var router = new ActiveChannelRouter(new FakeChannelLinkStore([older, newer]));

        var active = await router.SelectActiveAsync(
            Tenant, Conversation, CancellationToken.None);
        Assert.Equal(newer.Id, active!.Id);

        // Empate total em last_inbound_at E linked_at: o id decide, sempre igual.
        var twinLow = Link("01ARZ3NDEKTSV4RRFFQ69G5FC1", "telegram", sameInstant, Linked);
        var twinHigh = Link("01ARZ3NDEKTSV4RRFFQ69G5FC2", "whatsapp", sameInstant, Linked);
        var tieRouter = new ActiveChannelRouter(new FakeChannelLinkStore([twinLow, twinHigh]));
        var first = await tieRouter.SelectActiveAsync(
            Tenant, Conversation, CancellationToken.None);
        var second = await tieRouter.SelectActiveAsync(
            Tenant, Conversation, CancellationToken.None);
        Assert.Equal(twinHigh.Id, first!.Id);
        Assert.Equal(first.Id, second!.Id);
    }

    [Fact]
    public async Task LinksOfOtherConversationsNeverCompete()
    {
        var mine = Link("01ARZ3NDEKTSV4RRFFQ69G5FC1", "telegram", Linked.AddMinutes(1));
        var otherConversation = Link(
            "01ARZ3NDEKTSV4RRFFQ69G5FC2",
            "whatsapp",
            Linked.AddHours(9),
            conversationId: "01ARZ3NDEKTSV4RRFFQ69G5FZZ");
        var router = new ActiveChannelRouter(new FakeChannelLinkStore([mine, otherConversation]));

        var active = await router.SelectActiveAsync(
            Tenant, Conversation, CancellationToken.None);
        Assert.Equal(mine.Id, active!.Id);
        Assert.True(await router.IsActiveAsync(Tenant, mine, CancellationToken.None));
    }

    private static ChannelLinkRecord Link(
        string id,
        string kind,
        DateTimeOffset? lastInboundAt,
        DateTimeOffset? linkedAt = null,
        string conversationId = Conversation) =>
        new(
            Tenant,
            id,
            kind,
            $"{kind}-identity",
            "01ARZ3NDEKTSV4RRFFQ69G5FP1",
            "01ARZ3NDEKTSV4RRFFQ69G5FP2",
            conversationId,
            linkedAt ?? Linked,
            lastInboundAt);

    private sealed class FakeChannelLinkStore(IReadOnlyList<ChannelLinkRecord> links) : IChannelLinkStore
    {
        public Task<ChannelLinkRecord> GetOrCreateAsync(
            ChannelLinkCreateCommand command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ChannelLinkRecord?> GetAsync(
            string tenantId,
            string linkId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(links.FirstOrDefault(link =>
                link.TenantId == tenantId && link.Id == linkId));

        public Task<IReadOnlyList<ChannelLinkRecord>> ListAsync(
            string tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChannelLinkRecord>>(
                links.Where(link => link.TenantId == tenantId).ToArray());

        public Task MarkInboundAsync(
            string tenantId,
            string linkId,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
