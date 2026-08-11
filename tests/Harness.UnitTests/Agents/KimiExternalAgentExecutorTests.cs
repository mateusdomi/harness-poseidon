using Harness.Modules.Agents.Application.Execution.External;

namespace Harness.UnitTests.Agents;

public sealed class KimiExternalAgentExecutorTests
{
    [Fact]
    public void OAuthConnectionResetOnStandardErrorIsTypedAsTransientWithDiagnostic()
    {
        var parser = new KimiExternalAgentExecutor.KimiTextParser();

        parser.ObserveErrorLine(
            "error: failed to run prompt: internal: OAuth request to https://auth.kimi.com/api/oauth/token failed: fetch failed: read ECONNRESET");
        parser.Complete();

        Assert.Equal("executor.provider_unreachable", parser.FailureCode);
        Assert.Equal(ExternalFailureKind.Transient, parser.FailureKind);
        Assert.Contains("OAuth request", parser.FinalMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ECONNRESET", parser.FinalMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyOutputWithoutKnownKimiSentinelRemainsNoOutput()
    {
        var parser = new KimiExternalAgentExecutor.KimiTextParser();

        parser.Complete();

        Assert.Equal("executor.no_output", parser.FailureCode);
        Assert.Equal(ExternalFailureKind.Unknown, parser.FailureKind);
        Assert.Equal(string.Empty, parser.FinalMessage);
    }

    [Fact]
    public void NonEmptyOutputIsExplicitlyEstimatedUsage()
    {
        var parser = new KimiExternalAgentExecutor.KimiTextParser();

        _ = parser.ParseLine("resposta observável do kimi").ToArray();
        parser.Complete();

        Assert.NotNull(parser.Usage);
        Assert.Equal(ExternalAgentUsagePrecision.Estimated, parser.Usage!.Precision);
        Assert.Null(parser.Usage.InputTokens);
        Assert.True(parser.Usage.OutputTokens > 0);
    }
}
