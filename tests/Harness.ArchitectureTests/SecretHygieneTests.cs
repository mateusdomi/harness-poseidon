using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Harness.ArchitectureTests;

/// <summary>
/// Gate de segredos: falha se qualquer arquivo rastreado pelo Git contiver um segredo de
/// alta precisão (token de bot Telegram, chave privada, AWS/Slack/Google). Complementa a
/// auditoria de dependências (<c>NuGetAudit=all</c>). Padrões de alta precisão para não
/// produzir falso-positivo; o entrypoint operacional é <c>tools/backend/scan-secrets.sh</c>.
/// </summary>
public sealed partial class SecretHygieneTests
{
    private static readonly (string Name, Regex Pattern)[] SecretPatterns =
    [
        ("telegram-bot-token", TelegramToken()),
        ("private-key-block", PrivateKeyBlock()),
        ("aws-access-key-id", AwsAccessKey()),
        ("slack-token", SlackToken()),
        ("google-api-key", GoogleApiKey()),
    ];

    private static readonly string[] SkippedExtensions =
    [
        ".lock", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".svg",
        ".woff", ".woff2", ".ttf", ".otf", ".pdf", ".zip", ".mp4",
    ];

    private static readonly string[] SkippedPaths =
    [
        // SBOM: hashes longos gerados; nunca contém segredos, mas evita ruído de varredura.
        "docs/backend/security/sbom.json",
    ];

    [Fact]
    public void TrackedFilesContainNoHighSignalSecrets()
    {
        var root = FindRepositoryRoot();
        var findings = new List<string>();

        foreach (var relative in TrackedFiles(root))
        {
            if (ShouldSkip(relative))
            {
                continue;
            }

            var full = Path.Combine(root, relative);
            if (!File.Exists(full))
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(full);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var (name, pattern) in SecretPatterns)
            {
                if (pattern.IsMatch(content))
                {
                    findings.Add($"{relative}: {name}");
                }
            }
        }

        Assert.True(
            findings.Count == 0,
            "Possíveis segredos rastreados no Git:\n" + string.Join("\n", findings));
    }

    private static bool ShouldSkip(string relative)
    {
        if (SkippedPaths.Contains(relative, StringComparer.Ordinal))
        {
            return true;
        }

        var extension = Path.GetExtension(relative);
        return SkippedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static string[] TrackedFiles(string root)
    {
        var startInfo = new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git ls-files não pôde ser iniciado.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Não foi possível localizar a raiz contendo Harness.sln.");
    }

    [GeneratedRegex("[0-9]{6,10}:AA[A-Za-z0-9_-]{30,}", RegexOptions.CultureInvariant)]
    private static partial Regex TelegramToken();

    [GeneratedRegex("-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex("AKIA[0-9A-Z]{16}", RegexOptions.CultureInvariant)]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex("xox[baprs]-[0-9A-Za-z-]{10,}", RegexOptions.CultureInvariant)]
    private static partial Regex SlackToken();

    [GeneratedRegex("AIza[0-9A-Za-z_-]{35}", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleApiKey();
}
