using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Infrastructure.CodexCli;
using Harness.Modules.Agents.Infrastructure.Fake;

namespace Harness.UnitTests.Agents;

public sealed class AgentExecutorTests
{
    [Fact]
    public async Task FakeExecutorIsDeterministicAndProducesStrictChiefOutput()
    {
        var request = Request();
        var executor = new FakeAgentExecutor();

        var first = await executor.ExecuteAsync(request, CancellationToken.None);
        var replay = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal("fake", first.Executor);
        Assert.Equal(first.StructuredOutput, replay.StructuredOutput);
        Assert.Equal(first.Chunks, replay.Chunks);
        var output = ChiefTurnOutputContract.Parse(first.StructuredOutput);
        Assert.Contains("Continue com segurança", output.Response, StringComparison.Ordinal);
        Assert.Empty(output.Demands);
    }

    [Fact]
    public void ChiefOutputRejectsUnknownFieldsAndInvalidDemandRisk()
    {
        Assert.Throws<AgentOutputValidationException>(() =>
            ChiefTurnOutputContract.Parse("""{"response":"ok","demands":[],"hidden":true}"""));
        Assert.Throws<AgentOutputValidationException>(() =>
            ChiefTurnOutputContract.Parse(
                """{"response":"ok","demands":[{"title":"T","description":"D","riskTier":"extreme","acceptanceCriteria":["A"]}]}"""));
    }

    [Fact]
    public void ChiefOutputKeepsTheDeclarationStrictlyClosed()
    {
        // A superfície é o JULGAMENTO da chefe, não texto livre: propriedade desconhecida e valor
        // fora do tipo são recusados, do mesmo jeito que no resto do contrato.
        Assert.Throws<AgentOutputValidationException>(() =>
            ChiefTurnOutputContract.Parse(
                """{"response":"ok","demands":[{"title":"T","description":"D","riskTier":"low","acceptanceCriteria":["A"],"surfaces":{"mobile":true}}]}"""));
        Assert.Throws<AgentOutputValidationException>(() =>
            ChiefTurnOutputContract.Parse(
                """{"response":"ok","demands":[{"title":"T","description":"D","riskTier":"low","acceptanceCriteria":["A"],"surfaces":{"frontend":"sim"}}]}"""));
        Assert.Throws<AgentOutputValidationException>(() =>
            ChiefTurnOutputContract.Parse(
                """{"response":"ok","demands":[{"title":"T","description":"D","riskTier":"low","acceptanceCriteria":["A"],"surfaces":[]}]}"""));
    }

    [Fact]
    public void ADemandWithoutDeclarationParsesExactlyAsBefore()
    {
        // Compatibilidade: a declaração é OPCIONAL. Um turno sem ela continua válido e mantém a
        // inferência por texto — "não declarei" nunca vira "declarei que não".
        var output = ChiefTurnOutputContract.Parse(
            """{"response":"ok","demands":[{"title":"T","description":"D","riskTier":"low","acceptanceCriteria":["A"]}]}""");
        var demand = Assert.Single(output.Demands);
        Assert.Null(demand.Specialty);
        Assert.Null(demand.Surfaces);
    }

    [Fact]
    public void AnEmptySurfaceObjectIsTreatedAsNoDeclaration()
    {
        var output = ChiefTurnOutputContract.Parse(
            """{"response":"ok","demands":[{"title":"T","description":"D","riskTier":"low","acceptanceCriteria":["A"],"surfaces":{},"specialty":null}]}""");
        var demand = Assert.Single(output.Demands);
        Assert.Null(demand.Surfaces);
        Assert.Null(demand.Specialty);
    }

    [Fact]
    public void CodexExecutorRefusesIncompleteExternalSandboxProof()
    {
        var proof = new CodexCliExternalSandboxProof(true, true, false, true);
        Assert.Throws<ArgumentException>(() =>
            new CodexCliAgentExecutor(proof, _ => throw new InvalidOperationException()));
    }

    private static AgentExecutionRequest Request() => new(
        "01ARZ3NDEKTSV4RRFFQ69G5FAV",
        "01ARZ3NDEKTSV4RRFFQ69G5FAW",
        "01ARZ3NDEKTSV4RRFFQ69G5FAX",
        "01ARZ3NDEKTSV4RRFFQ69G5FAY",
        "Continue com segurança",
        "{}",
        Path.GetTempPath());
}
