using Harness.Host.Agents;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Providers.Application;
using Harness.Modules.Providers.Contracts;
using Harness.SharedKernel.Time;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.UnitTests.Agents;

/// <summary>
/// F-02: ChiefBacklogLoopService deve usar AgentAccountScheduler como único seletor de conta
/// critic. Estes testes garantem que SelectCriticAliases e CriticRosterReturnsBy preservam o
/// comportamento observável: actor≠critic, quota/cooldown bloqueiam, e a ordenação por prioridade
/// decrescente com desempate por alias continua determinística.
/// </summary>
public sealed class ChiefCriticSelectionTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly string _ledgerPath;

    public ChiefCriticSelectionTests()
    {
        _ledgerPath = Path.Combine(
            Path.GetTempPath(),
            $"harness-chief-critic-test-{Guid.NewGuid()}.json");
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_ledgerPath);
        }
        catch
        {
            // Melhor esforço: arquivo temporário, nada crítico.
        }
    }

    private static AgentAccountContract Account(
        string alias,
        string executorId,
        string role,
        AgentAccountState state = AgentAccountState.Available,
        int priority = 100,
        int concurrency = 1,
        int active = 0,
        DateTimeOffset? cooldownUntil = null) =>
        new(alias, "provider", executorId, $"keychain://poseidon/{alias}", $"confighome://{alias}",
            [role], [], state, AgentAccountHealth.Healthy, concurrency, active, null, null,
            cooldownUntil, null, null, priority);

    private static AgentAccountRegistry RegistryOf(params AgentAccountContract[] accounts)
    {
        var registry = new AgentAccountRegistry();
        foreach (var account in accounts)
        {
            registry.Register(account);
        }

        return registry;
    }

    private ChiefBacklogLoopService BuildService(
        AgentAccountRegistry registry,
        Action<CapacityManager>? configureCapacity = null,
        Action<AccountAvailabilityLedger>? configureAvailability = null)
    {
        var availability = new AccountAvailabilityLedger(_ledgerPath);
        configureAvailability?.Invoke(availability);

        var capacity = new CapacityManager();
        configureCapacity?.Invoke(capacity);

        // Aplica a disponibilidade observada ao registry, igual ao Host faz no bootstrap.
        _ = registry.ApplyObservedAvailability(availability.List());

        return new ChiefBacklogLoopService(
            scopes: null!,
            orchestrator: null!,
            registry,
            availability,
            policy: null!,
            new AgentRunSettings(),
            new FakeClock(Now),
            capacity,
            providerRouting: null!,
            codeGraph: null!,
            new AgentAccountScheduler(),
            NullLogger<ChiefBacklogLoopService>.Instance);
    }

    [Fact]
    public void ActorCannotBeCritic()
    {
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic));
        var service = BuildService(registry);

        var aliases = service.SelectCriticAliases("worker-codex-critic", Now);

        Assert.Empty(aliases);
    }

    [Fact]
    public void QuotaLimitedAccountIsExcludedFromSelection()
    {
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic),
            Account("worker-antigravity-review", ExecutorCatalog.Antigravity, AgentRoles.Critic, priority: 90));
        var service = BuildService(registry, capacity => capacity.UpdateQuotaSnapshot(
            "worker-antigravity-review",
            new QuotaStatusRecord(
                "capacity-manager", Now, "Exhausted", "High",
                RemainingFraction: 0, ResetAt: Now.AddHours(1), StaleAfter: TimeSpan.FromMinutes(5))));

        var aliases = service.SelectCriticAliases("worker-claude-secondary", Now);

        Assert.Equal(["worker-codex-critic"], aliases);
    }

    [Fact]
    public void CoolingDownAccountIsExcludedFromSelection()
    {
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic),
            Account("worker-antigravity-review", ExecutorCatalog.Antigravity, AgentRoles.Critic,
                state: AgentAccountState.CoolingDown, cooldownUntil: Now.AddMinutes(30), priority: 90));
        var service = BuildService(registry);

        var aliases = service.SelectCriticAliases("worker-claude-secondary", Now);

        Assert.Equal(["worker-codex-critic"], aliases);
    }

    [Fact]
    public void SelectionIsOrderedByPriorityDescendingThenAlias()
    {
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic, priority: 80),
            Account("worker-antigravity-review", ExecutorCatalog.Antigravity, AgentRoles.Critic, priority: 90),
            Account("worker-claude-critic", ExecutorCatalog.ClaudeCode, AgentRoles.Critic, priority: 90));
        var service = BuildService(registry);

        var aliases = service.SelectCriticAliases("some-actor", Now);

        Assert.Equal(
            ["worker-antigravity-review", "worker-claude-critic", "worker-codex-critic"],
            aliases);
    }

    [Fact]
    public void RosterReturnsByIsNowWhenAnyEligibleCriticExists()
    {
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic),
            Account("worker-antigravity-review", ExecutorCatalog.Antigravity, AgentRoles.Critic,
                state: AgentAccountState.CoolingDown, cooldownUntil: Now.AddMinutes(30)));
        var service = BuildService(registry);

        var returnsBy = service.CriticRosterReturnsBy("some-actor", Now);

        Assert.Equal(Now, returnsBy);
    }

    [Fact]
    public void RosterReturnsByIsEarliestCooldownWhenAllCriticsAreBlocked()
    {
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic,
                state: AgentAccountState.CoolingDown, cooldownUntil: Now.AddHours(2)),
            Account("worker-antigravity-review", ExecutorCatalog.Antigravity, AgentRoles.Critic,
                state: AgentAccountState.CoolingDown, cooldownUntil: Now.AddMinutes(30), priority: 90));
        var service = BuildService(registry);

        var returnsBy = service.CriticRosterReturnsBy("some-actor", Now);

        Assert.Equal(Now.AddMinutes(30), returnsBy);
    }

    [Fact]
    public void RosterReturnsByUsesLedgerCooldownForQuotaLimitedAccounts()
    {
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic));
        var service = BuildService(
            registry,
            configureAvailability: ledger => ledger.MarkQuotaLimited(
                "worker-codex-critic", Now.AddHours(1), "quota.exhausted", Now));

        var returnsBy = service.CriticRosterReturnsBy("some-actor", Now);

        Assert.Equal(Now.AddHours(1), returnsBy);
    }

    [Fact]
    public void RosterReturnsByIsNullWhenNoCriticExistsInRoster()
    {
        var registry = RegistryOf(
            Account("worker-claude-secondary", ExecutorCatalog.ClaudeCode, AgentRoles.BackendSpecialist));
        var service = BuildService(registry);

        var returnsBy = service.CriticRosterReturnsBy("worker-claude-secondary", Now);

        Assert.Null(returnsBy);
    }

    [Fact]
    public void RosterReturnsByIsNullWhenCriticsExistButHaveNoReturnWindow()
    {
        // Adapter não implementado ⇒ inelegível, mas NÃO é quota/cooldown, então não há janela.
        var registry = RegistryOf(
            Account("worker-kimi-critic", ExecutorCatalog.KimiCode, AgentRoles.Critic));
        var service = BuildService(registry);

        var returnsBy = service.CriticRosterReturnsBy("some-actor", Now);

        Assert.Null(returnsBy);
    }

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
