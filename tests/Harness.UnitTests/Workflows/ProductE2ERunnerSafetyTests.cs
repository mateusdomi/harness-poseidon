using Harness.Host.WorkBoard;

namespace Harness.UnitTests.Workflows;

public sealed class ProductE2ERunnerSafetyTests
{
    [Fact]
    public void E2EExecutadoComFalhaReprovaOGate()
    {
        var decision = ProductE2EGatePolicy.Decide(
            new ProductE2EResult(Ran: true, Passed: false, "1 failed"));

        Assert.Equal(ProductE2EGateDecision.Failed, decision);
    }

    [Fact]
    public void E2EIndisponivelNaoViraPass()
    {
        var decision = ProductE2EGatePolicy.Decide(
            new ProductE2EResult(Ran: false, Passed: false, "docker indisponível"));

        Assert.Equal(ProductE2EGateDecision.Unavailable, decision);
    }

    [Fact]
    public void NomeDoComposeEhIsoladoPorExecucao()
    {
        var first = ProductE2ERunner.ComposeProjectName("/tmp/produto");
        var second = ProductE2ERunner.ComposeProjectName("/tmp/produto");

        Assert.StartsWith("poseidon-e2e-", first, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void DetalheDeE2ENaoExpoeSecretsMaterializados()
    {
        const string secret = "SenhaSuperSecreta123!";
        var output = $"falha ao conectar usando Password={secret};User Id=APP";

        var redacted = ProductE2ERunner.RedactedTail(
            output,
            new Dictionary<string, string>
            {
                ["DB_PASSWORD"] = secret,
                ["ConnectionStrings__Default"] = $"User Id=APP;Password={secret}",
            });

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }
}
