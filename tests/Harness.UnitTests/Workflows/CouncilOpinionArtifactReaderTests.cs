using Harness.Host.Workflows;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// A opinião do conselheiro é o PARECER entregue. Estes testes fixam de onde ela é lida — porque
/// a fonte anterior (`work_attempts.summary`) nunca era escrita, e o conselho ficava incompleto
/// para sempre com os seis pareceres já entregues.
/// </summary>
public sealed class CouncilOpinionArtifactReaderTests
{
    [Fact]
    public void ExactPathWinsOverAnyOtherCouncilFile()
    {
        string[] changed =
        [
            "docs/conselho/playbook-qa-ciclo-1.md",
            "docs/conselho/playbook-po-ciclo-1.md",
        ];

        Assert.Equal(
            "docs/conselho/playbook-po-ciclo-1.md",
            GitCouncilOpinionArtifactReader.SelectParecer(changed, "playbook-po", 1));
    }

    [Fact]
    public void SingleCouncilFileIsAcceptedWhenTheNameDivergesFromTheConvention()
    {
        string[] changed = ["docs/conselho/parecer-po.md"];

        Assert.Equal(
            "docs/conselho/parecer-po.md",
            GitCouncilOpinionArtifactReader.SelectParecer(changed, "playbook-po", 2));
    }

    [Fact]
    public void AmbiguityIsAbsenceOfOpinionRatherThanAGuess()
    {
        string[] changed =
        [
            "docs/conselho/parecer-po.md",
            "docs/conselho/parecer-qa.md",
        ];

        Assert.Null(GitCouncilOpinionArtifactReader.SelectParecer(changed, "playbook-po", 1));
    }

    [Fact]
    public void DeliveryWithoutCouncilArtifactYieldsNoOpinion()
    {
        string[] changed = ["docs/architecture/sad.md", "docs/conselho/nota.txt"];

        Assert.Null(GitCouncilOpinionArtifactReader.SelectParecer(changed, "playbook-po", 1));
    }

    [Fact]
    public void ControlledRootIsMandatory() =>
        Assert.Throws<ArgumentException>(() => new GitCouncilOpinionArtifactReader("  "));
}
