using Harness.Modules.Workflows.Application;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// Decisão humana de arquitetura (2026-07-27): o dono do projeto é o stakeholder, não o operador
/// da esteira. O portão de fase passa a ser decidido pelo MODO que ele configurou — e Default-FAIL
/// deixa de ser confundido com "humano sempre obrigatório".
/// </summary>
public sealed class PhaseGatePolicyTests
{
    private static PhaseGateEvidence Ready() => new(
        HasGate: true,
        AllRequiredObligationsAccepted: true,
        HasBlockingFinding: false,
        HasOpenBlocker: false,
        RequiredObligationCount: 3);

    [Fact]
    public void AutonomousProjectHasItsGateDecidedByTheChief()
    {
        Assert.Equal(
            PhaseGateDecision.ChiefApproves,
            PhaseGatePolicy.Decide(
                ProjectOperationMode.Autonomous, "3-Arquitetura", "Aprovação", null, Ready()));
    }

    [Fact]
    public void ManualProjectAlwaysWaitsForTheOwner()
    {
        Assert.Equal(
            PhaseGateDecision.AwaitHuman,
            PhaseGatePolicy.Decide(
                ProjectOperationMode.Manual, "3-Arquitetura", "Aprovação", null, Ready()));
    }

    [Fact]
    public void SemiAutonomousBlocksOnlyThePhasesTheOwnerPicked()
    {
        Assert.Equal(
            PhaseGateDecision.AwaitHuman,
            PhaseGatePolicy.Decide(
                ProjectOperationMode.SemiAutonomous, "3-Arquitetura", "Aprovação",
                ["3-Arquitetura", "7-Homologação"], Ready()));
        Assert.Equal(
            PhaseGateDecision.ChiefApproves,
            PhaseGatePolicy.Decide(
                ProjectOperationMode.SemiAutonomous, "4-Planejamento", "Aprovação",
                ["3-Arquitetura", "7-Homologação"], Ready()));
    }

    [Fact]
    public void TheOwnerMayPickEitherThePhaseNameOrTheGateName()
    {
        // Ele marca fases numa tela; exigir que soubesse o vocabulário interno do portão faria a
        // configuração dele falhar em silêncio.
        Assert.Equal(
            PhaseGateDecision.AwaitHuman,
            PhaseGatePolicy.Decide(
                ProjectOperationMode.SemiAutonomous, "3-Arquitetura", "Aprovação de 3-Arquitetura",
                ["Aprovação de 3-Arquitetura"], Ready()));
    }

    [Theory]
    [InlineData(ProjectOperationMode.Autonomous)]
    [InlineData(ProjectOperationMode.SemiAutonomous)]
    [InlineData(ProjectOperationMode.Manual)]
    public void DefaultFailHoldsInEveryMode(ProjectOperationMode mode)
    {
        // Sem evidência o portão reprova nos três modos — é isto que Default-FAIL sempre quis
        // dizer. O que muda entre os modos é QUEM decide quando a evidência existe.
        Assert.Equal(
            PhaseGateDecision.NotReady,
            PhaseGatePolicy.Decide(mode, "5-Desenvolvimento", "Aprovação", null,
                Ready() with { AllRequiredObligationsAccepted = false }));
        Assert.Equal(
            PhaseGateDecision.NotReady,
            PhaseGatePolicy.Decide(mode, "5-Desenvolvimento", "Aprovação", null,
                Ready() with { HasBlockingFinding = true }));
        Assert.Equal(
            PhaseGateDecision.NotReady,
            PhaseGatePolicy.Decide(mode, "5-Desenvolvimento", "Aprovação", null,
                Ready() with { HasOpenBlocker = true }));
    }

    [Fact]
    public void AnEmptyPhaseIsNeverApproved()
    {
        // Aprovar uma fase sem obrigação obrigatória seria aprovar sem evidência nenhuma.
        Assert.Equal(
            PhaseGateDecision.NotReady,
            PhaseGatePolicy.Decide(
                ProjectOperationMode.Autonomous, "1-Triagem", "Aprovação", null,
                Ready() with { RequiredObligationCount = 0 }));
    }

    [Fact]
    public void AnUnknownModeFallsBackToTheOwner()
    {
        // Configuração que não entendemos devolve a decisão ao humano; assumir autonomia seria a
        // falha na direção perigosa.
        Assert.Equal(ProjectOperationMode.Manual, PhaseGatePolicy.ParseMode(null));
        Assert.Equal(ProjectOperationMode.Manual, PhaseGatePolicy.ParseMode("   "));
        Assert.Equal(ProjectOperationMode.Manual, PhaseGatePolicy.ParseMode("turbo"));
        Assert.Equal(ProjectOperationMode.Autonomous, PhaseGatePolicy.ParseMode("Autonomous"));
        Assert.Equal(ProjectOperationMode.SemiAutonomous, PhaseGatePolicy.ParseMode("semiautonomous"));
    }

    [Fact]
    public void SerializationRoundTripsEveryMode()
    {
        foreach (var mode in Enum.GetValues<ProjectOperationMode>())
        {
            Assert.Equal(mode, PhaseGatePolicy.ParseMode(PhaseGatePolicy.Serialize(mode)));
        }
    }
}
