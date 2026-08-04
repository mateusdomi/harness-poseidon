using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// A metade da camada determinística que independe do stack: varredura de segredo sobre o diff.
///
/// Os dois testes que sustentam o desenho são
/// <see cref="RemovingASecretIsNotAFinding"/> — punir a remoção ensinaria a não remover — e
/// <see cref="LegitimateAuthenticationCodeIsNotAFinding"/>: o gate só reconhece FORMATO concreto
/// de credencial, porque um falso-positivo aqui devolve o card para correções em laço, e o laço de
/// retrabalho é o gargalo de custo medido desta operação.
/// </summary>
public sealed class DeliverySecretScanGateTests
{
    private const string AwsFake = "AKIA" + "1234567890ABCDEF";
    private const string AwsKeyLine = "+const key = \"" + AwsFake + "\";";

    [Fact]
    public void EmptyDiffIsClean()
    {
        var verdict = DeliverySecretScanGate.Inspect(null);

        Assert.True(verdict.IsClean);
        Assert.Empty(verdict.Findings);
    }

    [Fact]
    public void AddedCredentialBlocksAndNamesTheFileAndTheFormat()
    {
        var verdict = DeliverySecretScanGate.Inspect(
            $"""
            diff --git a/src/Config.cs b/src/Config.cs
            --- a/src/Config.cs
            +++ b/src/Config.cs
            @@ -1,2 +1,3 @@
             public static class Config
            {AwsKeyLine}
            """);

        Assert.False(verdict.IsClean);
        var finding = Assert.Single(verdict.Findings);
        Assert.Equal("aws-access-key-id", finding.PatternName);
        Assert.Equal("src/Config.cs", finding.File);
    }

    [Fact]
    public void TheEvidenceNeverRepublishesTheSecret()
    {
        var verdict = DeliverySecretScanGate.Inspect($"+++ b/src/Config.cs\n{AwsKeyLine}");

        var finding = Assert.Single(verdict.Findings);
        Assert.DoesNotContain(AwsFake, finding.Excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain(AwsFake, verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("REDIGIDO", finding.Excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingASecretIsNotAFinding()
    {
        var verdict = DeliverySecretScanGate.Inspect(
            """
            +++ b/src/Config.cs
            -const key = "REMOVIDO_AQUI";
            +const key = Environment.GetEnvironmentVariable("AWS_KEY");
            """.Replace("REMOVIDO_AQUI", AwsFake, StringComparison.Ordinal));

        Assert.True(verdict.IsClean);
    }

    [Fact]
    public void TheFileHeaderItselfIsNotAnAddedLine()
    {
        // `+++ b/AKIA...` é cabeçalho de diff, não conteúdo entregue.
        var verdict = DeliverySecretScanGate.Inspect($"+++ b/docs/{AwsFake}.md");

        Assert.True(verdict.IsClean);
    }

    [Fact]
    public void LegitimateAuthenticationCodeIsNotAFinding()
    {
        var verdict = DeliverySecretScanGate.Inspect(
            """
            +++ b/src/Emprestimos/LoginService.cs
            +    var password = request.Password;
            +    var token = _issuer.Issue(user);
            +    options.ApiKey = configuration["Emprestimos:ApiKey"];
            +    // senha padrão do ambiente local: definida por variável de ambiente
            """);

        Assert.True(verdict.IsClean);
    }

    [Theory]
    [InlineData("+telegram = \"1234567890:AA" + "abcdefghijklmnopqrstuvwxyz012345678\"", "telegram-bot-token")]
    // Quebrado em duas partes como TODOS os irmãos abaixo: escrito inteiro, este literal casa o
    // padrão do `tools/backend/scan-secrets.sh` e deixa o gate de higiene do próprio repositório
    // vermelho — foi o único da lista que passou sem a quebra e derrubou o `verify.sh`.
    [InlineData("+-----BEGIN RSA PRIVATE " + "KEY-----", "private-key-block")]
    [InlineData("+slack = \"xox" + "b-1234567890abcdef\"", "slack-token")]
    [InlineData("+google = \"AIza" + "0123456789012345678901234567890abcd\"", "google-api-key")]
    [InlineData("+anthropic = \"sk-ant-" + "0123456789abcdefghij\"", "anthropic-api-key")]
    [InlineData("+openai = \"sk-" + "0123456789abcdefghijklmno\"", "openai-api-key")]
    [InlineData("+github = \"ghp_" + "0123456789abcdefghijklmnopqrst\"", "github-token")]
    public void EveryHighSignalFormatIsRecognized(string addedLine, string expectedPattern)
    {
        var verdict = DeliverySecretScanGate.Inspect($"+++ b/src/A.cs\n{addedLine}");

        Assert.False(verdict.IsClean);
        Assert.Equal(expectedPattern, Assert.Single(verdict.Findings).PatternName);
    }

    [Fact]
    public void ACleanScanNeverPromotesALayerThatDidNotRun()
    {
        // Mesmo contrato do gate de diagnósticos: o plug só PIORA o veredito. "Não achei segredo"
        // não é "compilou e testou".
        var notRun = new LayerResult(
            VerificationLayer.Deterministic, LayerVerdict.NotRun, "verification.build_and_tests");

        var applied = DeliverySecretScanGate.ApplyTo(notRun, DeliverySecretScanGate.Inspect(null));

        Assert.Equal(LayerVerdict.NotRun, applied.Verdict);
    }

    [Fact]
    public void ASecretFailsALayerThatOtherGatesHadApproved()
    {
        var passing = new LayerResult(
            VerificationLayer.Deterministic, LayerVerdict.Pass, CodeDiagnosticsGate.ReasonClean);

        var applied = DeliverySecretScanGate.ApplyTo(
            passing, DeliverySecretScanGate.Inspect($"+++ b/src/Config.cs\n{AwsKeyLine}"));

        Assert.Equal(LayerVerdict.Fail, applied.Verdict);
        Assert.Equal(DeliverySecretScanGate.ReasonSecretFound, applied.ReasonCode);
        Assert.False(LayeredVerificationPolicy.MayOccupyReviewer([applied]));
    }
}
