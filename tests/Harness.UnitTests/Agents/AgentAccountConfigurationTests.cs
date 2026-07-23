using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// CA-5: resolução de `--account <alias>` a partir da configuração local do operador.
/// </summary>
public sealed class AgentAccountConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"harness-accounts-config-{Guid.NewGuid():N}");

    private string WriteFile(string content)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, AgentAccountConfigurationLoader.FileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void TheSevenCanonicalAliasesExistWithoutAnyLocalConfiguration()
    {
        var registry = AgentAccountConfigurationLoader.Load(
            Path.Combine(_directory, "absent.json"));

        Assert.Equal(
            AgentAccountRegistry.SuggestedAliases.Order(StringComparer.Ordinal),
            registry.List().Select(account => account.Alias));
    }

    [Fact]
    public void NoAccountIsEverBornAvailable()
    {
        // Instalação e autenticação são COMPROVADAS por probe e login; disponibilidade
        // presumida foi exatamente o defeito que a RC3 mostrou.
        var registry = AgentAccountConfigurationLoader.Load(
            Path.Combine(_directory, "absent.json"));

        Assert.All(
            registry.List(),
            account => Assert.Equal(AgentAccountState.AuthenticationRequired, account.State));
        Assert.All(
            registry.List(),
            account => Assert.Equal(AgentAccountHealth.Unknown, account.Health));
    }

    [Fact]
    public void AntigravityIsAFirstClassCriticWithHigherPriorityThanTheCodexFallback()
    {
        var registry = AgentAccountConfigurationLoader.Load(
            Path.Combine(_directory, "absent.json"));

        var antigravity = registry.Get("worker-antigravity-review")!;
        var codexCritic = registry.Get("worker-codex-critic")!;

        Assert.Equal(ExecutorCatalog.Antigravity, antigravity.ExecutorId);
        Assert.Contains(AgentRoles.Critic, antigravity.AllowedRoles);
        Assert.Contains(AgentRoles.Critic, codexCritic.AllowedRoles);
        Assert.True(antigravity.Priority > codexCritic.Priority);
    }

    [Fact]
    public void TheFrontendRoleCarriesItsPathScopesAndNoOtherRoleDoes()
    {
        var registry = AgentAccountConfigurationLoader.Load(
            Path.Combine(_directory, "absent.json"));

        Assert.Equal(
            ["frontend/**", "docs/frontend/**"],
            registry.Get("worker-codex-frontend")!.AllowedPathScopes);
        Assert.Empty(registry.Get("chief-claude-primary")!.AllowedPathScopes);
        Assert.Empty(registry.Get("worker-antigravity-review")!.AllowedPathScopes);
    }

    [Fact]
    public void TheBackendRoleNowOwnsAWriteScopeEntirelyWithinItsBoundary()
    {
        // Gap fechado: o backend deixou de ter escopo vazio (que o recusava no agent-runs).
        var scopes = AgentRoles.PathScopesFor(AgentRoles.BackendSpecialist);
        Assert.NotEmpty(scopes);

        // Todo claim padrão do backend é aprovado pela política de escopo backend...
        var decision = Harness.Modules.Governance.Coordination.AgentPathScopePolicy.Evaluate(
            Harness.Modules.Governance.Coordination.AgentPathScopeKind.Backend, scopes);
        Assert.True(decision.Allowed);

        // ...e NENHUM deles alcança o frontend (a invariante inviolável).
        Assert.DoesNotContain(scopes, scope => scope.StartsWith("frontend/", StringComparison.Ordinal));
        Assert.DoesNotContain(scopes, scope => scope.StartsWith("docs/frontend/", StringComparison.Ordinal));

        // Frontend inalterado; papel desconhecido não recebe claim.
        Assert.Equal(["frontend/**", "docs/frontend/**"], AgentRoles.PathScopesFor(AgentRoles.FrontendSpecialist));
        Assert.Empty(AgentRoles.PathScopesFor(AgentRoles.ChiefOrchestrator));
    }

    [Fact]
    public void TheFrontendRoleIsProviderAgnostic()
    {
        // CA-1: Codex e Kimi Code exercem o MESMO papel lógico com o MESMO escopo.
        var registry = AgentAccountConfigurationLoader.Load(
            Path.Combine(_directory, "absent.json"));

        var codex = registry.Get("worker-codex-frontend")!;
        var kimi = registry.Get("worker-kimi-ui")!;

        Assert.NotEqual(codex.ExecutorId, kimi.ExecutorId);
        Assert.Equal(codex.AllowedRoles, kimi.AllowedRoles);
        Assert.Equal(codex.AllowedPathScopes, kimi.AllowedPathScopes);
    }

    [Fact]
    public void TheLocalFileOverridesACanonicalAliasWithoutRemovingTheOthers()
    {
        var path = WriteFile("""
        {
          "accounts": [
            {
              "alias": "worker-codex-frontend",
              "providerKind": "openai",
              "executorId": "codex",
              "credentialRef": "secret://poseidon/frontend",
              "allowedRoles": ["frontend-specialist"],
              "allowedPathScopes": ["frontend/**"],
              "concurrencyLimit": 2,
              "priority": 130
            }
          ]
        }
        """);

        var registry = AgentAccountConfigurationLoader.Load(path);
        var overridden = registry.Get("worker-codex-frontend")!;

        Assert.Equal("secret://poseidon/frontend", overridden.CredentialReference);
        Assert.Equal(2, overridden.ConcurrencyLimit);
        Assert.Equal(130, overridden.Priority);
        Assert.Equal(AgentAccountRegistry.SuggestedAliases.Count, registry.List().Count);
    }

    [Fact]
    public void ADisabledAccountIsDisabledAndNotSilentlyDropped()
    {
        var path = WriteFile("""
        {
          "accounts": [
            {
              "alias": "worker-glm-general",
              "providerKind": "zhipu",
              "executorId": "glm",
              "credentialRef": "keychain://poseidon/worker-glm-general",
              "enabled": false
            }
          ]
        }
        """);

        Assert.Equal(
            AgentAccountState.Disabled,
            AgentAccountConfigurationLoader.Load(path).Get("worker-glm-general")!.State);
    }

    [Fact]
    public void GlmNeverCarriesTheOldTokenBecauseOnlyOpaqueReferencesAreAccepted()
    {
        var path = WriteFile("""
        {
          "accounts": [
            {
              "alias": "worker-glm-general",
              "providerKind": "zhipu",
              "executorId": "glm",
              "credentialRef": "aa718a83d4354669a1e2e0b97f660bf4.SOMETHINGELSE"
            }
          ]
        }
        """);

        var exception = Assert.Throws<AgentAccountValidationException>(
            () => AgentAccountConfigurationLoader.Load(path));
        Assert.Equal("account.credential_reference_must_be_opaque", exception.Code);
    }

    [Fact]
    public void AnEmailInTheLocalFileIsRefused()
    {
        var path = WriteFile("""
        {
          "accounts": [
            {
              "alias": "pessoa@example.test",
              "providerKind": "openai",
              "executorId": "codex",
              "credentialRef": "keychain://poseidon/pessoa"
            }
          ]
        }
        """);

        var exception = Assert.Throws<AgentAccountValidationException>(
            () => AgentAccountConfigurationLoader.Load(path));
        Assert.Equal("account.alias_must_not_be_email", exception.Code);
    }

    [Fact]
    public void AnArbitraryRuntimeRoleInTheLocalFileIsRefused()
    {
        // CAT-09/ADR-022: o papel de runtime pertence ao conjunto canônico fechado. Um "papel de
        // negócio" novo (ex.: product-owner) é uma especialidade/persona no catálogo, nunca um papel
        // arbitrário na conta — a configuração é recusada de forma tipada em vez de conceder escopo
        // com base num rótulo desconhecido.
        var path = WriteFile("""
        {
          "accounts": [
            {
              "alias": "worker-codex-frontend",
              "providerKind": "openai",
              "executorId": "codex",
              "credentialRef": "keychain://poseidon/worker-codex-frontend",
              "allowedRoles": ["product-owner"]
            }
          ]
        }
        """);

        var exception = Assert.Throws<AgentAccountValidationException>(
            () => AgentAccountConfigurationLoader.Load(path));
        Assert.Equal("account.role_unknown", exception.Code);
    }

    [Fact]
    public void InvalidJsonFailsLoudlyInsteadOfSilentlyFallingBackToTheDefaults()
    {
        var path = WriteFile("{ not json");

        var exception = Assert.Throws<AgentAccountValidationException>(
            () => AgentAccountConfigurationLoader.Load(path));
        Assert.Equal("account.configuration_invalid", exception.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
