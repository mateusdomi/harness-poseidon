using Harness.Modules.Execution.Application.Git;
using Harness.Modules.Execution.Domain.Git;

namespace Harness.UnitTests.Execution;

public sealed class ScopeClaimRegistryTests
{
    [Theory]
    [InlineData("src/payments/**", "src/payments/checkout.cs", true)]
    [InlineData("src/payments/**", "src/orders/**", false)]
    [InlineData("docs/api.md", "docs/api.md", true)]
    [InlineData("src/api/**", "src/api-client/**", false)]
    public void IntersectionsAreSegmentAware(string left, string right, bool expected)
    {
        Assert.Equal(expected, new ScopeClaim(left).Intersects(new ScopeClaim(right)));
    }

    [Fact]
    public void AcquisitionIsIdempotentAndConflictsCannotReplaceClaims()
    {
        var registry = new ScopeClaimRegistry();
        var claims = new[] { new ScopeClaim("src/api/**") };

        Assert.True(registry.TryAcquire("attempt-a", claims).Acquired);
        Assert.True(registry.TryAcquire("attempt-a", claims).Acquired);

        var conflict = registry.TryAcquire("attempt-b", [new ScopeClaim("src/api/controller.cs")]);
        Assert.False(conflict.Acquired);
        Assert.Single(conflict.Conflicts);
        Assert.Equal("attempt-a", conflict.Conflicts[0].ExistingAttemptId);

        Assert.True(registry.Release("attempt-a"));
        Assert.True(registry.TryAcquire("attempt-b", [new ScopeClaim("src/api/controller.cs")]).Acquired);
    }

    [Fact]
    public void MultipleInstancesRunConcurrentlyOnDisjointSubtreesButCollideOnOverlap()
    {
        // Gap fechado: várias instâncias (mesma conta ou não) rodam em paralelo quando
        // reivindicam subárvores DISJUNTAS; o mesmo escopo continua colidindo (por design).
        var registry = new ScopeClaimRegistry();

        Assert.True(registry.TryAcquire("instance-1", [new ScopeClaim("src/Modules/Harness.Modules.Agents/**")]).Acquired);
        Assert.True(registry.TryAcquire("instance-2", [new ScopeClaim("src/Modules/Harness.Modules.Execution/**")]).Acquired);
        Assert.True(registry.TryAcquire("instance-3", [new ScopeClaim("tests/Harness.UnitTests/**")]).Acquired);

        // Uma quarta instância no MESMO subescopo da primeira é bloqueada.
        var collision = registry.TryAcquire("instance-4", [new ScopeClaim("src/Modules/Harness.Modules.Agents/Application/**")]);
        Assert.False(collision.Acquired);
        Assert.Equal("instance-1", collision.Conflicts[0].ExistingAttemptId);
    }
}
