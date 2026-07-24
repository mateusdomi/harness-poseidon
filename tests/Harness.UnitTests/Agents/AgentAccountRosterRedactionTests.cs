using System.Text.Json;
using Harness.Host.Agents;
using Harness.Modules.Agents.Application.Accounts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// Prova que o roster exposto no endpoint read-only NUNCA vaza segredo. A redação é
/// estrutural (o contrato não tem campo de credencial), então o teste ataca por dois
/// flancos: forma do contrato e serialização JSON completa.
/// </summary>
public sealed class AgentAccountRosterRedactionTests
{
    [Fact]
    public void TheRosterSurfacesTheSevenExecutionIdentities()
    {
        var response = AgentRunEndpoints.RedactRoster(
            AgentAccountConfigurationLoader.CanonicalDefinitions);

        Assert.Equal(7, response.Accounts.Count);
        Assert.Contains(response.Accounts, account => account.Alias == "chief-claude-primary");
        Assert.Contains(response.Accounts, account => account.Alias == "worker-antigravity-review");
        Assert.All(response.Accounts, account => Assert.False(string.IsNullOrWhiteSpace(account.ProviderKind)));
        Assert.All(response.Accounts, account => Assert.NotEmpty(account.Roles));
    }

    [Fact]
    public void TheContractHasNoCredentialPropertyAtAll()
    {
        var properties = typeof(AgentAccountRosterContract)
            .GetProperties()
            .Select(property => property.Name);

        Assert.DoesNotContain("CredentialRef", properties);
        Assert.DoesNotContain("CredentialReference", properties);
        Assert.DoesNotContain(properties, name =>
            name.Contains("Credential", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheSerializedRosterNeverContainsACredentialReferenceOrToken()
    {
        // Mesmo com uma conta local cuja credencial é uma referência opaca conhecida, a
        // serialização do roster não pode conter nenhum vestígio dela.
        var definitions = new List<AgentAccountDefinition>
        {
            new()
            {
                Alias = "worker-glm-general",
                ProviderKind = "zhipu",
                ExecutorId = "glm",
                CredentialRef = "keychain://poseidon/worker-glm-general",
                AllowedRoles = ["backend-specialist"],
            },
        };

        var json = JsonSerializer.Serialize(AgentRunEndpoints.RedactRoster(definitions));

        Assert.DoesNotContain("keychain", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confighome", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        // ...mas o dado seguro está presente.
        Assert.Contains("worker-glm-general", json, StringComparison.Ordinal);
        Assert.Contains("zhipu", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisabledAccountReportsDisabledState()
    {
        var definitions = new List<AgentAccountDefinition>
        {
            new()
            {
                Alias = "worker-kimi-ui",
                ProviderKind = "moonshot",
                ExecutorId = "kimi-code",
                CredentialRef = "keychain://poseidon/worker-kimi-ui",
                AllowedRoles = ["frontend-specialist"],
                Enabled = false,
            },
        };

        var account = Assert.Single(AgentRunEndpoints.RedactRoster(definitions).Accounts);
        Assert.False(account.Enabled);
        Assert.Equal("disabled", account.State);
    }
}
