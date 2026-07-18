namespace Harness.Modules.Agents.Infrastructure.CodexCli;

public static class CodexCliExecutableLocator
{
    public static string Find(string? path = null)
    {
        var configuredPath = Environment.GetEnvironmentVariable("HARNESS_CODEX_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullConfiguredPath = Path.GetFullPath(configuredPath);
            if (!File.Exists(fullConfiguredPath))
            {
                throw new FileNotFoundException(
                    "HARNESS_CODEX_CLI_PATH does not identify an existing executable.",
                    fullConfiguredPath);
            }

            return fullConfiguredPath;
        }

        var searchPath = path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "codex");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException(
            "Codex CLI was not found. Install it or set HARNESS_CODEX_CLI_PATH to its executable path.");
    }
}
