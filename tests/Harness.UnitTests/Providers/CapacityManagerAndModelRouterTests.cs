using Harness.Modules.Providers.Application;
using Harness.Modules.Providers.Contracts;
using Harness.SharedKernel.Providers;

namespace Harness.UnitTests.Providers;

public sealed class CapacityManagerAndModelRouterTests
{
    private static SimpleAccountSpec CreateAccount(
        string alias,
        string role = "backend-specialist",
        int priority = 100)
    {
        return new SimpleAccountSpec(
            Alias: alias,
            ProviderKind: "anthropic",
            AllowedRoles: [role],
            AllowedPathScopes: ["src/**"],
            ConcurrencyLimit: 2,
            ActiveAttempts: 0,
            Priority: priority);
    }

    [Fact]
    public void ProviderQuotaCollectorCollectsExhaustedFromQuotaExceededOutcome()
    {
        var collector = new ProviderQuotaCollector();
        var now = DateTimeOffset.UtcNow;

        var invocations = new List<ModelInvocationRecord>
        {
            new("inv-1", "tenant-1", "proj-1", "task-1", "att-1", "anthropic", "claude-3-7-sonnet",
                "worker-1", 100, 50, 0.01m, 200, "quota_exceeded", now.AddMinutes(-5))
        };

        var snapshot = collector.Collect("worker-1", "anthropic", invocations, now);

        Assert.Equal("Exhausted", snapshot.Status);
        Assert.Equal("High", snapshot.Confidence);
    }

    [Fact]
    public void CapacityManagerTripsCircuitBreakerAfterThresholdFailures()
    {
        var manager = new CapacityManager(consecutiveFailureThreshold: 3);
        var now = DateTimeOffset.UtcNow;

        manager.RecordInvocationOutcome("worker-1", "anthropic", "error", now);
        manager.RecordInvocationOutcome("worker-1", "anthropic", "error", now);
        Assert.False(manager.IsCircuitTripped("worker-1", now));

        manager.RecordInvocationOutcome("worker-1", "anthropic", "error", now);
        Assert.True(manager.IsCircuitTripped("worker-1", now));

        var snapshot = manager.GetQuotaSnapshot("worker-1", now);
        Assert.Equal("Exhausted", snapshot.Status);
    }

    [Fact]
    public void CapacityManagerEvaluatesBackpressureWhenAllExhausted()
    {
        var manager = new CapacityManager();
        var now = DateTimeOffset.UtcNow;

        var acc1 = CreateAccount("worker-1");
        var acc2 = CreateAccount("worker-2");

        manager.UpdateQuotaSnapshot("worker-1", new QuotaStatusRecord(
            "test", now, "Exhausted", "High", 0.0, now.AddMinutes(10), TimeSpan.FromMinutes(15)));

        manager.UpdateQuotaSnapshot("worker-2", new QuotaStatusRecord(
            "test", now, "Exhausted", "High", 0.0, now.AddMinutes(5), TimeSpan.FromMinutes(15)));

        var capacity = manager.EvaluateCapacity([acc1, acc2], "backend-specialist", now);

        Assert.True(capacity.IsUnderBackpressure);
        Assert.NotNull(capacity.BackpressureResetAt);
        Assert.Equal(now.AddMinutes(5), capacity.BackpressureResetAt);
    }

    [Fact]
    public void ModelRouterRoutesToHighestPriorityEligibleAccount()
    {
        var accounts = new List<SimpleAccountSpec>
        {
            CreateAccount("primary", priority: 200),
            CreateAccount("fallback", priority: 100)
        };

        var manager = new CapacityManager();
        var router = new ModelRouter(manager);
        var now = DateTimeOffset.UtcNow;

        var request = new ModelRoutingRequest(
            Role: "backend-specialist",
            RequiredCapability: "code",
            PreferredModel: "claude-3-7-sonnet",
            RiskTier: "low",
            ActorAlias: null,
            ForCritic: false,
            RequiredPathScopes: ["src/**"],
            Now: now);

        var decision = router.Route(accounts, request);

        Assert.Equal("primary", decision.SelectedAlias);
        Assert.Equal("claude-3-7-sonnet", decision.SelectedModel);
        Assert.False(decision.IsFallback);
    }

    [Fact]
    public void ModelRouterFallsBackWhenPrimaryIsQuotaExhausted()
    {
        var accounts = new List<SimpleAccountSpec>
        {
            CreateAccount("primary", priority: 200),
            CreateAccount("fallback", priority: 100)
        };

        var manager = new CapacityManager();
        var now = DateTimeOffset.UtcNow;

        manager.UpdateQuotaSnapshot("primary", new QuotaStatusRecord(
            "test", now, "Exhausted", "High", 0.0, now.AddMinutes(15), TimeSpan.FromMinutes(15)));

        var router = new ModelRouter(manager);

        var request = new ModelRoutingRequest(
            Role: "backend-specialist",
            RequiredCapability: "code",
            PreferredModel: "claude-3-7-sonnet",
            RiskTier: "low",
            ActorAlias: null,
            ForCritic: false,
            RequiredPathScopes: ["src/**"],
            Now: now);

        var decision = router.Route(accounts, request);

        Assert.Equal("fallback", decision.SelectedAlias);
        Assert.True(decision.IsFallback);
    }
}
