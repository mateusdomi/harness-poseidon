using System.Text.RegularExpressions;

namespace Harness.SharedKernel.Security;

public static partial class SecretTextProtector
{
    private const string Redacted = "[REDACTED]";

    public static bool ContainsSecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return TelegramToken().IsMatch(value) ||
            PrivateKeyBlock().IsMatch(value) ||
            AwsAccessKey().IsMatch(value) ||
            SlackToken().IsMatch(value) ||
            GoogleApiKey().IsMatch(value) ||
            NamedSecret().IsMatch(value);
    }

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var redacted = NamedSecret().Replace(value, $"$1={Redacted}");
        redacted = TelegramToken().Replace(redacted, Redacted);
        redacted = PrivateKeyBlock().Replace(redacted, $"-----BEGIN {Redacted} PRIVATE KEY-----");
        redacted = AwsAccessKey().Replace(redacted, Redacted);
        redacted = SlackToken().Replace(redacted, Redacted);
        return GoogleApiKey().Replace(redacted, Redacted);
    }

    public static bool ContainsSensitiveCommandArgument(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(argument =>
            ContainsSecret(argument) || SensitiveArgument().IsMatch(argument));
    }

    public static void ThrowIfSensitiveCommandArguments(
        IReadOnlyList<string> arguments,
        string parameterName)
    {
        if (ContainsSensitiveCommandArgument(arguments))
        {
            throw new ArgumentException(
                "Sensitive values and credential switches are forbidden in command arguments; use an environment-backed secret reference.",
                parameterName);
        }
    }

    [GeneratedRegex("[0-9]{6,10}:AA[A-Za-z0-9_-]{30,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TelegramToken();

    [GeneratedRegex("-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex("AKIA[0-9A-Z]{16}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex("xox[baprs]-[0-9A-Za-z-]{10,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SlackToken();

    [GeneratedRegex("AIza[0-9A-Za-z_-]{35}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex GoogleApiKey();

    [GeneratedRegex("(?i)\\b(token|api[_-]?key|secret|password)\\s*[:=]\\s*[^\\s,;]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex NamedSecret();

    [GeneratedRegex("(?i)^--?(?:token|api[_-]?key|secret|password)(?:=|$)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SensitiveArgument();
}
