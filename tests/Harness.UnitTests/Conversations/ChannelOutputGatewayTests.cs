using Harness.Host.Conversations;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.UnitTests.Conversations;

/// <summary>
/// Output Gateway: a regra inviolável de que somente a Bruna publica precisa ser verificável,
/// não confiada. Toda tentativa de publicar em nome de outro autor — ou com correlação de
/// tenant/projeto/conversa divergente — é bloqueada E registrada no ledger.
/// </summary>
public sealed class ChannelOutputGatewayTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FP2";
    private const string Conversation = "01ARZ3NDEKTSV4RRFFQ69G5FB1";
    private const string ChiefAgent = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AllowsOnlyChiefMessagesOnTheActiveChannel()
    {
        var link = Link("01ARZ3NDEKTSV4RRFFQ69G5FC1", "telegram");
        var (gateway, audit) = Gateway(link);

        var authorization = await gateway.AuthorizeAsync(
            Tenant, link, Message("chief"), Now, CancellationToken.None);

        Assert.True(authorization.Allowed);
        Assert.Equal("allowed", authorization.ResultCode);
        // Publicação legítima não polui o ledger append-only: ela já é um span correlacionado.
        Assert.Empty(audit.Appended);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("user")]
    [InlineData("system")]
    public async Task DeniesAndAuditsAnyAuthorThatIsNotTheChief(string authorRole)
    {
        var link = Link("01ARZ3NDEKTSV4RRFFQ69G5FC1", "telegram");
        var (gateway, audit) = Gateway(link);

        var authorization = await gateway.AuthorizeAsync(
            Tenant, link, Message(authorRole), Now, CancellationToken.None);

        Assert.False(authorization.Allowed);
        Assert.Equal(ChannelOutputDecision.DeniedNotChief, authorization.Decision);
        var entry = Assert.Single(audit.Appended);
        Assert.Equal("channel.publication.denied", entry.Action);
        Assert.Equal("channel_link", entry.TargetType);
        Assert.Equal(link.Id, entry.TargetId);

        Assert.Contains(authorRole, entry.Detail!, StringComparison.Ordinal);
        // O conteúdo da mensagem jamais entra no ledger.
        Assert.DoesNotContain("segredo-do-usuario", entry.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeniesAndAuditsCrossConversationOrCrossTenantPublication()
    {
        var link = Link("01ARZ3NDEKTSV4RRFFQ69G5FC1", "telegram");
        var (gateway, audit) = Gateway(link);

        var otherConversation = await gateway.AuthorizeAsync(
            Tenant,
            link,
            Message("chief") with { ConversationId = "01ARZ3NDEKTSV4RRFFQ69G5FZZ" },
            Now,
            CancellationToken.None);
        Assert.Equal(ChannelOutputDecision.DeniedCorrelationMismatch, otherConversation.Decision);

        var otherTenant = await gateway.AuthorizeAsync(
            Tenant,
            link,
            Message("chief") with { TenantId = "01ARZ3NDEKTSV4RRFFQ69G5FXX" },
            Now,
            CancellationToken.None);
        Assert.Equal(ChannelOutputDecision.DeniedCorrelationMismatch, otherTenant.Decision);

        var otherProject = await gateway.AuthorizeAsync(
            Tenant,
            link,
            Message("chief") with { ProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FYY" },
            Now,
            CancellationToken.None);
        Assert.Equal(ChannelOutputDecision.DeniedCorrelationMismatch, otherProject.Decision);

        Assert.Equal(3, audit.Appended.Count);
        Assert.All(audit.Appended, entry =>
            Assert.Equal("channel.publication.denied", entry.Action));
    }

    [Fact]
    public async Task DeniesChiefRoleWhenPersistedChiefIdentityDoesNotMatch()
    {
        var link = Link("01ARZ3NDEKTSV4RRFFQ69G5FC1", "telegram");
        var (gateway, audit) = Gateway(link);

        var authorization = await gateway.AuthorizeAsync(
            Tenant,
            link,
            Message("chief") with { AuthorAgentId = "agent-impersonating-chief" },
            Now,
            CancellationToken.None);

        Assert.False(authorization.Allowed);
        Assert.Equal(
            ChannelOutputDecision.DeniedChiefIdentityMismatch,
            authorization.Decision);
        Assert.Equal(
            "denied_chief_identity_mismatch",
            authorization.ResultCode);
        Assert.Equal("channel.publication.denied", Assert.Single(audit.Appended).Action);
    }

    [Fact]
    public async Task SkipsInactiveChannelWithoutTreatingItAsViolation()
    {
        var telegram = Link("01ARZ3NDEKTSV4RRFFQ69G5FC1", "telegram", Now.AddMinutes(1));
        var whatsapp = Link("01ARZ3NDEKTSV4RRFFQ69G5FC2", "whatsapp", Now.AddMinutes(9));
        var (gateway, audit) = Gateway(telegram, whatsapp);

        var inactive = await gateway.AuthorizeAsync(
            Tenant, telegram, Message("chief"), Now, CancellationToken.None);
        Assert.False(inactive.Allowed);
        Assert.Equal(ChannelOutputDecision.SkippedInactiveChannel, inactive.Decision);
        Assert.Equal("skipped_inactive_channel", inactive.ResultCode);

        var activeChannel = await gateway.AuthorizeAsync(
            Tenant, whatsapp, Message("chief"), Now, CancellationToken.None);
        Assert.True(activeChannel.Allowed);

        // Roteamento não é violação: nada vai para o ledger.
        Assert.Empty(audit.Appended);
    }

    private static (ChannelOutputGateway Gateway, RecordingAuditStore Audit) Gateway(
        params ChannelLinkRecord[] links)
    {
        var store = new FakeChannelLinkStore(links);
        var audit = new RecordingAuditStore();
        return (
            new ChannelOutputGateway(
                new ActiveChannelRouter(store),
                new FakeProjectStore(),
                audit,
                NullLogger<ChannelOutputGateway>.Instance),
            audit);
    }

    private static MessageRecord Message(string authorRole) => new(
        Tenant,
        Project,
        "01ARZ3NDEKTSV4RRFFQ69G5FM1",
        Conversation,
        authorRole,
        null,
        ChiefAgent,
        "segredo-do-usuario",
        null,
        Now);

    private static ChannelLinkRecord Link(
        string id,
        string kind,
        DateTimeOffset? lastInboundAt = null) =>
        new(Tenant, id, kind, $"{kind}-identity", "01ARZ3NDEKTSV4RRFFQ69G5FP1",
            Project, Conversation, Now, lastInboundAt);

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
            Task.FromResult(links.FirstOrDefault(link => link.Id == linkId));

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

    private sealed class FakeProjectStore : IProjectStore
    {
        public Task<ProjectRecord?> GetAsync(
            string tenantId,
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectRecord?>(
                tenantId == Tenant && projectId == Project
                    ? new ProjectRecord(
                        Tenant,
                        Project,
                        "organization-1",
                        "Project",
                        "PRJ",
                        "Description",
                        "active",
                        "medium",
                        null,
                        "github",
                        "develop",
                        [],
                        new ProjectBrandRecord(null, null, null, null),
                        [],
                        1,
                        ChiefAgent,
                        "autonomous",
                        Now,
                        Now,
                        1)
                    : null);

        public Task<IReadOnlyList<ProjectRecord>> ListAsync(
            string tenantId,
            string? afterId,
            int limit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProjectMutationResult> CreateAsync(
            ProjectCreateCommand command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProjectMutationResult> UpdateAsync(
            ProjectUpdateCommand command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProjectMutationResult> DeleteAsync(
            string tenantId,
            string projectId,
            long expectedVersion,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingAuditStore : IAuditEventStore
    {
        public List<AuditEventAppendCommand> Appended { get; } = [];

        public Task<AuditEventRecord> AppendAsync(
            AuditEventAppendCommand command,
            CancellationToken cancellationToken = default)
        {
            Appended.Add(command);
            return Task.FromResult(new AuditEventRecord(
                "01ARZ3NDEKTSV4RRFFQ69G5FE1",
                command.ActorKind,
                command.ActorId,
                command.Action,
                command.TargetType,
                command.TargetId,
                command.Detail,
                command.OccurredAt));
        }

        public Task<IReadOnlyList<AuditEventRecord>> ListAsync(
            AuditEventQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditEventRecord>>([]);

        public Task<AuditEventRecord?> GetAsync(
            string tenantId,
            string id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AuditEventRecord?>(null);

        public Task<AuditIntegrityRecord> VerifyIntegrityAsync(
            string tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuditIntegrityRecord(true, Appended.Count, Appended.Count, "", null));
    }
}
