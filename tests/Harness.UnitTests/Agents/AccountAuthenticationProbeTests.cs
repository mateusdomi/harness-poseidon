using Harness.Host.Agents;
using Harness.Modules.Agents.Application.Accounts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// A detecção de autenticação por CLI (doctor) é observada por PRESENÇA no config home
/// isolado. Cada executor grava o material em local diferente — Codex (`auth.json`),
/// Antigravity (`.gemini/antigravity-cli/antigravity-oauth-token`), Claude (`.claude.json`
/// com `oauthAccount`) e GLM (token de ambiente). Procurar o arquivo errado produz FALSO
/// NEGATIVO (o bug que reportava o Antigravity autenticado como não autenticado).
/// </summary>
public sealed class AccountAuthenticationProbeTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), $"harness-auth-{Guid.NewGuid():N}");

    public AccountAuthenticationProbeTests() => Directory.CreateDirectory(_home);

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_home, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void CodexIsAuthenticatedByAuthJson()
    {
        Assert.False(AccountAuthenticationProbe.HasMaterial(_home, ExecutorCatalog.Codex, false));
        Write("auth.json", "{}");
        Assert.True(AccountAuthenticationProbe.HasMaterial(_home, ExecutorCatalog.Codex, false));
    }

    [Fact]
    public void AntigravityIsAuthenticatedByItsOAuthTokenFileNotByClaudeJson()
    {
        // O bug: o Antigravity autenticado (token presente) era reportado como não
        // autenticado porque a detecção procurava `.claude.json`.
        Assert.False(AccountAuthenticationProbe.HasMaterial(_home, ExecutorCatalog.Antigravity, false));
        Write(Path.Combine(".gemini", "antigravity-cli", "antigravity-oauth-token"), "opaque");
        Assert.True(AccountAuthenticationProbe.HasMaterial(_home, ExecutorCatalog.Antigravity, false));
    }

    [Fact]
    public void ClaudeIsAuthenticatedOnlyWhenClaudeJsonCarriesAnOAuthAccount()
    {
        Assert.False(AccountAuthenticationProbe.HasMaterial(_home, ExecutorCatalog.ClaudeCode, false));
        Write(".claude.json", """{"someOther":"value"}""");
        Assert.False(AccountAuthenticationProbe.HasMaterial(_home, ExecutorCatalog.ClaudeCode, false));
        Write(".claude.json", """{"oauthAccount":{"present":true}}""");
        Assert.True(AccountAuthenticationProbe.HasMaterial(_home, ExecutorCatalog.ClaudeCode, false));
    }

    [Fact]
    public void KimiIsAuthenticatedByItsIsolatedCredentialFileUnderTheHomeConfig()
    {
        Assert.False(AccountAuthenticationProbe.HasMaterial(_home, ExecutorCatalog.KimiCode, false));
        Write(Path.Combine(".kimi-code", "credentials", "kimi-code.json"), "{}");
        Assert.True(AccountAuthenticationProbe.HasMaterial(_home, ExecutorCatalog.KimiCode, false));
    }

    [Fact]
    public void GlmIsAuthenticatedByTheEnvironmentTokenEvenWithoutACredentialFile()
    {
        // GLM autentica por ANTHROPIC_AUTH_TOKEN (env), não por arquivo no dir.
        Assert.False(AccountAuthenticationProbe.HasMaterial(_home, ExecutorCatalog.Glm, glmTokenPresent: false));
        Assert.True(AccountAuthenticationProbe.HasMaterial(_home, ExecutorCatalog.Glm, glmTokenPresent: true));
    }

    [Fact]
    public void AMissingConfigHomeIsNeverAuthenticated()
    {
        var absent = Path.Combine(_home, "does-not-exist");
        Assert.False(AccountAuthenticationProbe.HasMaterial(absent, ExecutorCatalog.Codex, false));
        Assert.False(AccountAuthenticationProbe.HasMaterial(absent, ExecutorCatalog.Antigravity, false));
        Assert.False(AccountAuthenticationProbe.HasMaterial(absent, ExecutorCatalog.ClaudeCode, false));
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }
}
