using Harness.SharedKernel.Security;

namespace Harness.UnitTests.Security;

public sealed class SecretTextProtectorTests
{
    [Fact]
    public void TelegramIncidentRegressionR013DetectsAndRedactsWithoutDisclosure()
    {
        var token = string.Concat(
            "123456789",
            ":AA",
            new string('x', 30));
        var loggedUri = $"https://api.telegram.org/bot{token}/getUpdates";

        Assert.True(SecretTextProtector.ContainsSecret(loggedUri));
        var redacted = SecretTextProtector.Redact(loggedUri);
        Assert.DoesNotContain(token, redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--token")]
    [InlineData("--api-key=secret-reference")]
    [InlineData("-password")]
    public void CredentialSwitchesAreForbiddenInProcessArguments(string argument)
    {
        Assert.Throws<ArgumentException>(() =>
            SecretTextProtector.ThrowIfSensitiveCommandArguments([argument], "arguments"));
    }

    [Fact]
    public void OrdinaryProcessArgumentsRemainAllowed()
    {
        SecretTextProtector.ThrowIfSensitiveCommandArguments(
            ["app-server", "--listen", "stdio://"],
            "arguments");
    }
}
