using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// O cérebro do despacho do chefe: por prioridade e papel, manda cada card para uma conta
/// DISPONÍVEL (respeitando cota do ledger e concorrência), e adia o resto com o motivo —
/// incluindo QUANDO a conta volta. É a decisão auditável que o operador ajusta.
/// </summary>
public sealed class ChiefBacklogPolicyTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"harness-chief-{Guid.NewGuid():N}");

    private AccountAvailabilityLedger Ledger() =>
        new(System.IO.Path.Combine(_dir, "avail.json"));

    private static AgentAccountRegistry RegistryWith(params (string Alias, string Executor, string Role, int Concurrency)[] accounts)
    {
        var registry = new AgentAccountRegistry();
        foreach (var (alias, executor, role, concurrency) in accounts)
        {
            registry.Register(new AgentAccountContract(
                alias, "provider", executor, $"keychain://poseidon/{alias}", $"confighome://{alias}",
                [role], role == AgentRoles.FrontendSpecialist ? ["frontend/**", "docs/frontend/**"] : [],
                AgentAccountState.Available, AgentAccountHealth.Healthy, concurrency, 0, null,
                null, null, null, null, 100));
        }

        return registry;
    }

    private static ChiefCard Card(string taskId, string role, int priority) =>
        new(taskId, "project-1", role, role == AgentRoles.Critic ? "review" : "code", priority, []);

    [Fact]
    public void CardsAreDispatchedByPriorityToAnAvailableAccountOfTheirRole()
    {
        var registry = RegistryWith(
            ("worker-glm-general", ExecutorCatalog.Glm, AgentRoles.BackendSpecialist, 3),
            ("worker-codex-frontend", ExecutorCatalog.Codex, AgentRoles.FrontendSpecialist, 3));

        var backlog = new[]
        {
            Card("t-low", AgentRoles.BackendSpecialist, 10),
            Card("t-high", AgentRoles.BackendSpecialist, 90),
            Card("t-front", AgentRoles.FrontendSpecialist, 50),
        };

        var plan = new ChiefBacklogPolicy().Plan(backlog, registry, Ledger(), maxConcurrentDispatch: 5, Now);

        Assert.Equal(3, plan.Dispatch.Count);
        // Maior prioridade primeiro.
        Assert.Equal("t-high", plan.Dispatch[0].Card.TaskId);
        Assert.Equal("worker-glm-general", plan.Dispatch[0].AccountAlias);
        Assert.Equal("worker-codex-frontend", plan.Dispatch.Single(d => d.Card.TaskId == "t-front").AccountAlias);
        Assert.Empty(plan.Deferred);
    }

    [Fact]
    public void NeverDispatchesMoreThanTheAccountConcurrencyInOneRound()
    {
        // Uma conta com limite 2 recebe no máximo 2 cards nesta rodada; o 3º é adiado.
        var registry = RegistryWith(("worker-glm-general", ExecutorCatalog.Glm, AgentRoles.BackendSpecialist, 2));
        var backlog = new[]
        {
            Card("a", AgentRoles.BackendSpecialist, 30),
            Card("b", AgentRoles.BackendSpecialist, 20),
            Card("c", AgentRoles.BackendSpecialist, 10),
        };

        var plan = new ChiefBacklogPolicy().Plan(backlog, registry, Ledger(), maxConcurrentDispatch: 5, Now);

        Assert.Equal(2, plan.Dispatch.Count);
        Assert.Equal("c", Assert.Single(plan.Deferred).Card.TaskId);
    }

    [Fact]
    public void ACardWhoseOnlyAccountIsQuotaLimitedIsDeferredWithItsReturnTime()
    {
        var registry = RegistryWith(("worker-glm-general", ExecutorCatalog.Glm, AgentRoles.BackendSpecialist, 3));
        var ledger = Ledger();
        var returnsAt = Now.AddHours(3);
        ledger.MarkQuotaLimited("worker-glm-general", returnsAt, "run.quota_exhausted", Now);

        var plan = new ChiefBacklogPolicy().Plan(
            [Card("t", AgentRoles.BackendSpecialist, 50)], registry, ledger, maxConcurrentDispatch: 5, Now);

        Assert.Empty(plan.Dispatch);
        var deferral = Assert.Single(plan.Deferred);
        Assert.Equal("chief.awaiting_account_return", deferral.ReasonCode);
        Assert.Equal(returnsAt, deferral.RetryAfter);
    }

    [Fact]
    public void WhenTheQuotaWindowHasResetTheAccountIsDispatchedAgain()
    {
        var registry = RegistryWith(("worker-glm-general", ExecutorCatalog.Glm, AgentRoles.BackendSpecialist, 3));
        var ledger = Ledger();
        ledger.MarkQuotaLimited("worker-glm-general", Now.AddHours(3), "run.quota_exhausted", Now);

        // Já passou o reset (a conta ainda consta no ledger com cooldown vencido).
        var later = Now.AddHours(4);
        var plan = new ChiefBacklogPolicy().Plan(
            [Card("t", AgentRoles.BackendSpecialist, 50)], registry, ledger, maxConcurrentDispatch: 5, later);

        Assert.Equal("worker-glm-general", Assert.Single(plan.Dispatch).AccountAlias);
    }

    [Fact]
    public void TheGlobalDispatchBudgetCapsHowManyRunAtOnce()
    {
        var registry = RegistryWith(("worker-glm-general", ExecutorCatalog.Glm, AgentRoles.BackendSpecialist, 10));
        var backlog = Enumerable.Range(0, 5)
            .Select(i => Card($"t{i}", AgentRoles.BackendSpecialist, 50 - i))
            .ToArray();

        var plan = new ChiefBacklogPolicy().Plan(backlog, registry, Ledger(), maxConcurrentDispatch: 2, Now);

        Assert.Equal(2, plan.Dispatch.Count);
        Assert.Equal(3, plan.Deferred.Count);
        Assert.All(plan.Deferred, d => Assert.Equal("chief.dispatch_budget_reached", d.ReasonCode));
    }

    [Fact]
    public void ACardWithNoAccountForItsRoleIsDeferredWithoutARetryTime()
    {
        // Nenhuma conta de backend; o card de backend não tem para onde ir e não tem hora de volta.
        var registry = RegistryWith(("worker-codex-frontend", ExecutorCatalog.Codex, AgentRoles.FrontendSpecialist, 3));

        var plan = new ChiefBacklogPolicy().Plan(
            [Card("t", AgentRoles.BackendSpecialist, 50)], registry, Ledger(), maxConcurrentDispatch: 5, Now);

        Assert.Empty(plan.Dispatch);
        Assert.Null(Assert.Single(plan.Deferred).RetryAfter);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
