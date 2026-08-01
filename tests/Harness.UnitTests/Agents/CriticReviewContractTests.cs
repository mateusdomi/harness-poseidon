using Harness.Host.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// CA-7: o veredito do critic é Default-FAIL. Nenhuma ambiguidade vira aprovação.
/// </summary>
public sealed class CriticReviewContractTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentOutputIsFailNeverPass(string? output)
    {
        var (verdict, reason, _, _) = CriticReviewContract.Parse(output);
        Assert.Equal(CriticVerdict.Fail, verdict);
        Assert.Equal("critic.no_output", reason);
    }

    [Theory]
    [InlineData("Achei tudo ótimo, aprovado!")]
    [InlineData("{ isso nao e json valido")]
    public void UnstructuredOrInvalidOutputIsFail(string output)
    {
        var (verdict, reason, _, _) = CriticReviewContract.Parse(output);
        Assert.Equal(CriticVerdict.Fail, verdict);
        Assert.Contains("critic.output_not_json", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AVerdictOutsideTheClosedSetIsFail()
    {
        var (verdict, _, _, _) = CriticReviewContract.Parse(
            """{"verdict":"aprovado","summary":"ok","findings":[]}""");
        Assert.Equal(CriticVerdict.Fail, verdict);
    }

    [Fact]
    public void AMissingVerdictIsFail()
    {
        var (verdict, reason, _, _) = CriticReviewContract.Parse(
            """{"summary":"parece bom","findings":[]}""");
        Assert.Equal(CriticVerdict.Fail, verdict);
        Assert.Equal("critic.verdict_missing", reason);
    }

    [Fact]
    public void APassContradictedByASevereFindingIsFail()
    {
        // Um critic que aprova e ao mesmo tempo relata P0/P1 está incoerente; prevalece o
        // achado, nunca a aprovação.
        var (verdict, reason, findings, _) = CriticReviewContract.Parse(
            """
            {"verdict":"pass","summary":"ok","findings":[
              {"severity":"P0","code":"suite-vermelha","summary":"teste falhando"}]}
            """);

        Assert.Equal(CriticVerdict.Fail, verdict);
        Assert.Equal("critic.pass_contradicted_by_findings", reason);
        Assert.Single(findings);
    }

    [Fact]
    public void ACoherentPassIsAccepted()
    {
        var (verdict, reason, findings, summary) = CriticReviewContract.Parse(
            """
            {"verdict":"pass","summary":"critérios atendidos","findings":[
              {"severity":"P3","code":"estilo","summary":"nome poderia ser melhor"}],
             "checks":{"delegationCompared":true,"scopeVerified":true,
               "evidenceSufficient":true,"unsupportedClaims":[],"unlabeledInferences":[]}}
            """);

        Assert.Equal(CriticVerdict.Pass, verdict);
        Assert.Equal("critic.pass", reason);
        Assert.Equal("critérios atendidos", summary);
        Assert.Equal(CriticFindingSeverity.P3, Assert.Single(findings).Severity);
    }

    [Fact]
    public void APassWithoutMaterializedChecksFailsClosed()
    {
        var (verdict, reason, _, _) = CriticReviewContract.Parse(
            """{"verdict":"pass","summary":"parece bom","findings":[]}""");

        Assert.Equal(CriticVerdict.Fail, verdict);
        Assert.Equal("critic.checks_missing", reason);
    }

    [Theory]
    [InlineData("unsupportedClaims", "critic.unsupported_claims")]
    [InlineData("unlabeledInferences", "critic.unlabeled_inferences")]
    public void APassWithAnUnresolvedClaimAuditFailsClosed(string field, string expectedReason)
    {
        var unsupported = field == "unsupportedClaims" ? "[\"valor inventado\"]" : "[]";
        var unlabeled = field == "unlabeledInferences"
            ? "[\"dedução repetida sem rótulo\"]"
            : "[]";
        var output =
            "{\"verdict\":\"pass\",\"summary\":\"otimista\",\"findings\":[]," +
            "\"checks\":{\"delegationCompared\":true,\"scopeVerified\":true," +
            "\"evidenceSufficient\":true,\"unsupportedClaims\":" + unsupported + "," +
            "\"unlabeledInferences\":" + unlabeled + "}}";

        var (verdict, reason, _, _) = CriticReviewContract.Parse(output);
        Assert.Equal(CriticVerdict.Fail, verdict);
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void TheVerdictIsReadEvenWhenTheModelWrapsItInText()
    {
        // Modelos frequentemente cercam o JSON com prosa; isso não pode virar FAIL por
        // tecnicalidade, mas o objeto precisa existir e ser válido.
        var (verdict, _, findings, _) = CriticReviewContract.Parse(
            """
            Segue minha avaliação.

            {"verdict":"fail","summary":"suite vermelha","findings":[
              {"severity":"P1","code":"regressao","summary":"1 teste quebrou","path":"a.tsx"}]}
            """);

        Assert.Equal(CriticVerdict.Fail, verdict);
        Assert.Equal("a.tsx", Assert.Single(findings).Path);
    }

    [Fact]
    public void FindingsCarryTheClosedSeverityScale()
    {
        var (_, _, findings, _) = CriticReviewContract.Parse(
            """
            {"verdict":"fail","summary":"x","findings":[
              {"severity":"P0","code":"a","summary":"a"},
              {"severity":"P1","code":"b","summary":"b"},
              {"severity":"P2","code":"c","summary":"c"},
              {"severity":"P3","code":"d","summary":"d"},
              {"severity":"desconhecida","code":"e","summary":"e"}]}
            """);

        Assert.Equal(
            [
                CriticFindingSeverity.P0, CriticFindingSeverity.P1, CriticFindingSeverity.P2,
                CriticFindingSeverity.P3,
                // Severidade fora da escala não some: cai no meio, nunca em "sem problema".
                CriticFindingSeverity.P2,
            ],
            findings.Select(finding => finding.Severity));
    }
}
