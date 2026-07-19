using Harness.Modules.Governance.Domain;

namespace Harness.UnitTests.Governance;

public sealed class AutonomousActionGuardTests
{
    private static readonly GuardedActionKind[] AllActions =
        Enum.GetValues<GuardedActionKind>();

    private static readonly string[] AllModes =
        ["manual", "semiautonomous", "autonomous"];

    private static readonly GuardedActorKind[] AutomatedActors =
        [GuardedActorKind.Chief, GuardedActorKind.Agent, GuardedActorKind.System];

    [Fact]
    public void EveryGuardedActionIsDeniedForEveryAutomatedActorInEveryMode()
    {
        foreach (var action in AllActions)
        {
            foreach (var mode in AllModes)
            {
                foreach (var actor in AutomatedActors)
                {
                    var decision = AutonomousActionGuard.Evaluate(
                        new GuardedActionRequest(action, actor, mode));
                    Assert.False(
                        decision.Allowed,
                        $"A ação {action} foi permitida para {actor} em modo {mode}.");
                    Assert.Equal(AutonomousActionGuard.CodeFor(action), decision.Code);
                }
            }
        }
    }

    [Fact]
    public void AutomatedActorCannotBypassWithForgedApprovalReference()
    {
        foreach (var action in AllActions)
        {
            var decision = AutonomousActionGuard.Evaluate(new GuardedActionRequest(
                action,
                GuardedActorKind.Agent,
                "autonomous",
                HumanApprovalId: "01ARZ3NDEKTSV4RRFFQ69G5FAV"));
            Assert.False(
                decision.Allowed,
                $"A ação {action} foi permitida a um agente com aprovação forjada.");
        }
    }

    [Fact]
    public void HumanWithoutRecordedApprovalIsDenied()
    {
        foreach (var action in AllActions)
        {
            var decision = AutonomousActionGuard.Evaluate(new GuardedActionRequest(
                action,
                GuardedActorKind.Human,
                "manual"));
            Assert.False(decision.Allowed);
        }
    }

    [Fact]
    public void HumanWithExplicitApprovalPassesTheGuard()
    {
        foreach (var action in AllActions)
        {
            var decision = AutonomousActionGuard.Evaluate(new GuardedActionRequest(
                action,
                GuardedActorKind.Human,
                "manual",
                HumanApprovalId: "01ARZ3NDEKTSV4RRFFQ69G5FAV"));
            Assert.True(decision.Allowed);
        }
    }

    [Fact]
    public void UnknownOperationModeIsRejectedAsClosedSetViolation()
    {
        Assert.Throws<ArgumentException>(() => AutonomousActionGuard.Evaluate(
            new GuardedActionRequest(
                GuardedActionKind.ProductionDeploy,
                GuardedActorKind.Agent,
                "unrestricted")));
    }
}
