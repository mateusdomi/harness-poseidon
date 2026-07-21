using Harness.Host.Execution;

namespace Harness.IntegrationTests.Execution;

/// <summary>
/// CA-1: a identidade do papel de frontend é provider-agnostic na configuração do Host.
/// </summary>
public sealed class FrontendRoleIdentityTests
{
    [Theory]
    [InlineData("frontend-specialist")]
    [InlineData("frontend-codex")]
    [InlineData("codex-frontend")]
    [InlineData("frontend-kimi")]
    [InlineData("kimi-code")]
    [InlineData("kimi")]
    public void AnyAuthorizedExecutorCanHoldTheFrontendRole(string definitionKey)
    {
        var settings = new IsolatedExecutionSettings();
        Assert.Contains(
            definitionKey, settings.FrontendRoleDefinitionKeys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyConfigurationKeyStillOverridesTheFrontendRoleList()
    {
        var settings = new IsolatedExecutionSettings { KimiAgentDefinitionKeys = ["only-this"] };
        Assert.Equal(["only-this"], settings.FrontendRoleDefinitionKeys);
        Assert.DoesNotContain(
            "frontend-codex", settings.FrontendRoleDefinitionKeys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackendDefinitionsNeverHoldTheFrontendRole()
    {
        var settings = new IsolatedExecutionSettings();
        Assert.DoesNotContain(
            "software-engineer", settings.FrontendRoleDefinitionKeys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "chief-orchestrator", settings.FrontendRoleDefinitionKeys, StringComparer.OrdinalIgnoreCase);
    }
}
