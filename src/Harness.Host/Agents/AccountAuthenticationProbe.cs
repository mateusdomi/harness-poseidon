using System.Text.Json;
using Harness.Modules.Agents.Application.Accounts;

namespace Harness.Host.Agents;

/// <summary>
/// Detecção PURA da autenticação observada no config home isolado de uma conta (CA-3).
///
/// O sinal difere por CLI e foi verificado nas instaladas nesta máquina:
/// - Codex grava <c>auth.json</c> no config home;
/// - o Antigravity (`agy`) grava <c>.gemini/antigravity-cli/antigravity-oauth-token</c>;
/// - o Claude Code guarda o token no Keychain e registra a conta vinculada em
///   <c>.claude.json</c> (objeto <c>oauthAccount</c>);
/// - o GLM é o Claude Code apontado a um endpoint compatível por TOKEN de ambiente
///   (<c>ANTHROPIC_AUTH_TOKEN</c>), então a autenticação é a presença do token, não um
///   arquivo no diretório.
///
/// Só a PRESENÇA é observada. O <c>oauthAccount</c> contém identidade real (inclusive
/// e-mail) e seu conteúdo nunca é lido, copiado, logado ou propagado.
/// </summary>
public static class AccountAuthenticationProbe
{
    public static bool HasMaterial(string configHomePath, string executorId, bool glmTokenPresent)
    {
        if (string.Equals(executorId, ExecutorCatalog.Glm, StringComparison.Ordinal))
        {
            // O token de ambiente autentica o GLM mesmo sem arquivo de credencial no dir.
            return glmTokenPresent || HasClaudeOAuthAccount(configHomePath);
        }

        if (string.IsNullOrEmpty(configHomePath) || !Directory.Exists(configHomePath))
        {
            return false;
        }

        return executorId switch
        {
            ExecutorCatalog.Codex => File.Exists(Path.Combine(configHomePath, "auth.json")),
            ExecutorCatalog.Antigravity => File.Exists(Path.Combine(
                configHomePath, ".gemini", "antigravity-cli", "antigravity-oauth-token")),
            _ => HasClaudeOAuthAccount(configHomePath),
        };
    }

    private static bool HasClaudeOAuthAccount(string configHomePath)
    {
        if (string.IsNullOrEmpty(configHomePath))
        {
            return false;
        }

        var descriptor = Path.Combine(configHomePath, ".claude.json");
        if (!File.Exists(descriptor))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(descriptor));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("oauthAccount", out var account) &&
                account.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
