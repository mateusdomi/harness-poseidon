using System.Text.RegularExpressions;

namespace Harness.Modules.Agents.Application.Execution.External;

/// <summary>
/// Redaction estrutural da saída de executores externos (CA-4).
///
/// A saída de um CLI de agente é conteúdo NÃO CONFIÁVEL: pode ecoar um token que o modelo
/// leu de um arquivo, uma variável de ambiente ou um cabeçalho. Nada sai do adapter para
/// evento, log, receipt ou evidência sem passar por aqui.
/// </summary>
public static partial class ExternalAgentRedaction
{
    public const string Placeholder = "[REDACTED]";

    /// <summary>Substitui valores com forma de segredo por um marcador estável.</summary>
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = AnthropicKey().Replace(value, Placeholder);
        redacted = OpenAiKey().Replace(redacted, Placeholder);
        redacted = GitHubToken().Replace(redacted, Placeholder);
        redacted = BearerHeader().Replace(redacted, $"$1 {Placeholder}");
        redacted = PrivateKeyBlock().Replace(redacted, Placeholder);
        redacted = AssignedSecret().Replace(redacted, $"$1={Placeholder}");
        return redacted;
    }

    /// <summary>Verdadeiro quando o texto AINDA aparenta conter segredo após a redaction.</summary>
    public static bool ContainsSecret(string? value) =>
        !string.IsNullOrEmpty(value) &&
        (AnthropicKey().IsMatch(value) ||
            OpenAiKey().IsMatch(value) ||
            GitHubToken().IsMatch(value) ||
            BearerHeader().IsMatch(value) ||
            PrivateKeyBlock().IsMatch(value) ||
            AssignedSecret().IsMatch(value));

    [GeneratedRegex("sk-ant-[A-Za-z0-9_-]{16,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AnthropicKey();

    [GeneratedRegex("sk-(?:proj-)?[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex OpenAiKey();

    [GeneratedRegex("gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex GitHubToken();

    [GeneratedRegex("(?i)\\b(bearer)\\s+[A-Za-z0-9._~+/=-]{20,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex BearerHeader();

    [GeneratedRegex("-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(
        "(?i)\\b([A-Za-z0-9_]*(?:token|secret|password|passwd|api[_-]?key|auth)[A-Za-z0-9_]*)\\s*[=:]\\s*[\"']?[A-Za-z0-9._~+/=-]{12,}[\"']?",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AssignedSecret();
}
