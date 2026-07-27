using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// Cenário 4.8 da auditoria: sem executor adequado, a chefe BLOQUEIA e COMUNICA — nunca atribui
/// para qualquer agente disponível. O bloqueio já era garantido pelo scheduler fail-closed; o que
/// faltava era distinguir a recusa que o tempo resolve daquela que exige ação, para que só a
/// segunda chegue ao dono.
/// </summary>
public sealed class ChiefDeferralTriageTests
{
    private static ChiefDeferral Deferral(params AccountSelectionCandidate[] candidates) =>
        new(
            new ChiefCard("01ARZ3NDEKTSV4RRFFQ69G5FAV", "01ARZ3NDEKTSV4RRFFQ69G5FAW",
                AgentRoles.BackendSpecialist, "code", 50, ["src/**"]),
            "scheduler.no_eligible_account",
            null,
            candidates);

    private static AccountSelectionCandidate Candidate(string alias, string reason, bool eligible = false) =>
        new(alias, eligible, reason, 100);

    [Fact]
    public void NoAccountHasTheRoleIsStructural()
    {
        Assert.True(ChiefDeferralTriage.IsStructural(Deferral(
            Candidate("worker-a", "account.role_not_allowed"),
            Candidate("worker-b", "account.role_not_allowed"))));
    }

    [Fact]
    public void AMissingAdapterOrCapabilityIsStructural()
    {
        Assert.True(ChiefDeferralTriage.IsStructural(Deferral(
            Candidate("worker-kimi", "account.adapter_not_implemented"),
            Candidate("worker-b", "account.capability_unsupported"))));
    }

    [Fact]
    public void QuotaAndCooldownAreNotStructuralBecauseTimeResolvesThem()
    {
        Assert.False(ChiefDeferralTriage.IsStructural(Deferral(
            Candidate("worker-a", "account.quota_limited"),
            Candidate("worker-b", "account.cooling_down"))));
        Assert.False(ChiefDeferralTriage.IsStructural(Deferral(
            Candidate("worker-a", "account.concurrency_exhausted"))));
    }

    [Fact]
    public void OneAccountThatCouldStillTakeItKeepsTheCaseAsOrdinaryWaiting()
    {
        // Enquanto existir quem ainda pode assumir, não há o que anunciar.
        Assert.False(ChiefDeferralTriage.IsStructural(Deferral(
            Candidate("worker-a", "account.role_not_allowed"),
            Candidate("worker-b", "account.quota_limited"))));
        Assert.False(ChiefDeferralTriage.IsStructural(Deferral(
            Candidate("worker-a", "account.role_not_allowed"),
            Candidate("worker-b", "account.eligible", eligible: true))));
    }

    [Fact]
    public void WithoutEvaluatedCandidatesThereIsNoVerdict()
    {
        // Ausência de dado nunca vira conclusão.
        Assert.False(ChiefDeferralTriage.IsStructural(Deferral()));
        Assert.False(ChiefDeferralTriage.IsStructural(
            new ChiefDeferral(
                new ChiefCard("01ARZ3NDEKTSV4RRFFQ69G5FAV", "01ARZ3NDEKTSV4RRFFQ69G5FAW",
                    AgentRoles.BackendSpecialist, "code", 50, ["src/**"]),
                "chief.dispatch_budget_reached",
                null)));
    }

    [Fact]
    public void AnUnknownReasonCodeIsNeverTreatedAsStructural()
    {
        // Conservador de propósito: avisar demais treina o dono a ignorar o aviso.
        Assert.False(ChiefDeferralTriage.IsStructural(Deferral(
            Candidate("worker-a", "account.some_new_reason_added_later"))));
    }
}
