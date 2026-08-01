using Harness.Host.Agents;

namespace Harness.UnitTests.Agents;

public sealed class ChiefTeamActionResponseTests
{
    [Theory]
    [InlineData("Criei uma especialista para cuidar desta parte.")]
    [InlineData("A nova profissional já está na equipe.")]
    [InlineData("Incorporamos a pessoa especializada ao projeto.")]
    public void ATeamChangeCannotBeAnnouncedBeforePersistence(string response)
    {
        Assert.True(ChiefTeamActionResponse.ContainsPrematureCompletionClaim(response));
    }

    [Fact]
    public void APlannedChangeDoesNotPretendTheEffectAlreadyHappened()
    {
        Assert.False(ChiefTeamActionResponse.ContainsPrematureCompletionClaim(
            "Vou incorporar uma especialista para cuidar desta parte."));
    }

    [Fact]
    public void ASuccessfulStoreResultProducesBusinessConfirmation()
    {
        var projected = ChiefTeamActionResponse.Project(
            "Vou organizar a equipe para conduzir essa frente.",
            [new ChiefTeamActionResult(
                "create_persona", "team.persona_allowed", "accessibility-specialist", true)]);

        Assert.Contains("organização da equipe foi confirmada", projected, StringComparison.Ordinal);
        Assert.Contains("disponíveis no projeto", projected, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedStoreResultDoesNotClaimThatTheProfessionalExists()
    {
        var projected = ChiefTeamActionResponse.Project(
            "Vou organizar a equipe para conduzir essa frente.",
            [new ChiefTeamActionResult(
                "create_persona", "team.no_executable_route", "accessibility-specialist", false)]);

        Assert.Contains("Ainda não consegui concluir", projected, StringComparison.Ordinal);
        Assert.DoesNotContain("foi confirmada", projected, StringComparison.Ordinal);
    }
}
