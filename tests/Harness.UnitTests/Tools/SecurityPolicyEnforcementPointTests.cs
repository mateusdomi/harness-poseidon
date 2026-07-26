using Harness.Modules.Tools.Application;

namespace Harness.UnitTests.Tools;

public sealed class SecurityPolicyEnforcementPointTests
{
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 7, 26, 16, 0, 0, TimeSpan.Zero));
    private readonly RecordingAuditSink _audit = new();

    [Fact]
    public async Task ExactSpecialistCapabilityIsAllowedWithoutExposingBearerValue()
    {
        var sut = CreateSut();
        var token = sut.Issue(Grant());

        var decision = await sut.AuthorizeAsync(token, Authorization());

        Assert.True(decision.Allowed);
        Assert.Equal("[REDACTED CAPABILITY]", token.ToString());
        Assert.Collection(_audit.Records, record =>
        {
            Assert.True(record.Allowed);
            Assert.Equal(token.Id, record.CapabilityId);
            Assert.Equal("capability_allowed", record.Code);
        });
    }

    [Theory]
    [InlineData("tenant", "capability_tenant_mismatch")]
    [InlineData("project", "capability_project_mismatch")]
    [InlineData("card", "capability_card_mismatch")]
    [InlineData("attempt", "capability_attempt_mismatch")]
    [InlineData("tool", "capability_tool_denied")]
    [InlineData("resource", "capability_resource_denied")]
    [InlineData("path", "capability_path_denied")]
    [InlineData("path_wildcard", "capability_path_denied")]
    [InlineData("fencing", "capability_fencing_mismatch")]
    public async Task BoundaryMismatchIsDeniedAndAudited(string boundary, string expectedCode)
    {
        var sut = CreateSut();
        var token = sut.Issue(Grant());
        var request = Authorization();
        request = boundary switch
        {
            "tenant" => request with { TenantId = "tenant-other" },
            "project" => request with { ProjectId = "project-other" },
            "card" => request with { CardId = "card-other" },
            "attempt" => request with { AttemptId = "attempt-other" },
            "tool" => request with { ToolId = "tool-other" },
            "resource" => request with { ResourceId = "resource-other" },
            "path" => request with { RelativePath = "frontend/src/app.tsx" },
            "path_wildcard" => request with { RelativePath = "src/**" },
            "fencing" => request with { FencingToken = 8 },
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };

        var decision = await sut.AuthorizeAsync(token, request);

        Assert.False(decision.Allowed);
        Assert.Equal(expectedCode, decision.Code);
        Assert.Equal(expectedCode, Assert.Single(_audit.Records).Code);
    }

    [Fact]
    public async Task ExpiredAndRevokedCapabilitiesFailClosed()
    {
        var sut = CreateSut();
        var expired = sut.Issue(Grant());
        _time.Advance(TimeSpan.FromMinutes(6));

        var expiredDecision = await sut.AuthorizeAsync(expired, Authorization());

        Assert.Equal("capability_expired", expiredDecision.Code);

        var active = sut.Issue(Grant() with { ExpiresAt = _time.GetUtcNow().AddMinutes(5) });
        Assert.True(sut.Revoke(active));
        var revokedDecision = await sut.AuthorizeAsync(active, Authorization());

        Assert.Equal("capability_invalid", revokedDecision.Code);
        Assert.Equal(2, _audit.Records.Count);
    }

    [Fact]
    public async Task OnlyChiefCanUseExternalPublicationCapability()
    {
        var sut = CreateSut();
        var specialistToken = sut.Issue(Grant() with
        {
            Operation = CapabilityOperation.ExternalPublication,
            AllowedToolIds = [],
            AllowedPathClaims = [],
        });
        var denied = await sut.AuthorizeAsync(
            specialistToken,
            Authorization() with
            {
                Operation = CapabilityOperation.ExternalPublication,
                ToolId = null,
                RelativePath = null,
            });
        var chiefToken = sut.Issue(Grant() with
        {
            ActorKind = CapabilityActorKind.Chief,
            ActorId = "bruna",
            Operation = CapabilityOperation.ExternalPublication,
            AllowedToolIds = [],
            AllowedPathClaims = [],
        });
        var allowed = await sut.AuthorizeAsync(
            chiefToken,
            Authorization() with
            {
                ActorKind = CapabilityActorKind.Chief,
                ActorId = "bruna",
                Operation = CapabilityOperation.ExternalPublication,
                ToolId = null,
                RelativePath = null,
            });

        Assert.Equal("publication_actor_denied", denied.Code);
        Assert.True(allowed.Allowed);
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("/absolute/path")]
    [InlineData("src/*/file.cs")]
    public void InvalidPathClaimsAreRejectedAtIssuance(string path)
    {
        var sut = CreateSut();

        Assert.Throws<ArgumentException>(() => sut.Issue(Grant() with { AllowedPathClaims = [path] }));
    }

    private SecurityPolicyEnforcementPoint CreateSut() => new(_audit, _time);

    private CapabilityGrantRequest Grant() => new(
        CapabilityActorKind.Specialist,
        "agent-1",
        "tenant-1",
        "project-1",
        "card-1",
        "attempt-1",
        CapabilityOperation.ToolExecution,
        ["tool-1"],
        ["workspace"],
        ["src/Modules/Harness.Modules.Tools/**"],
        _time.GetUtcNow().AddMinutes(5),
        7);

    private static CapabilityAuthorizationRequest Authorization() => new(
        CapabilityActorKind.Specialist,
        "agent-1",
        "tenant-1",
        "project-1",
        "card-1",
        "attempt-1",
        CapabilityOperation.ToolExecution,
        "tool-1",
        "workspace",
        "src/Modules/Harness.Modules.Tools/Application/IToolExecutor.cs",
        7);

    private sealed class RecordingAuditSink : ICapabilityDecisionAuditSink
    {
        public List<CapabilityDecisionAuditRecord> Records { get; } = [];

        public ValueTask RecordAsync(
            CapabilityDecisionAuditRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
