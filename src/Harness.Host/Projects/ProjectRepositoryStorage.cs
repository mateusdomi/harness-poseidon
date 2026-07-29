using System.Diagnostics;
using Harness.SharedKernel.Identifiers;

namespace Harness.Host.Projects;

/// <summary>
/// Creates repositories owned by Poseidon under one controlled data directory.
/// User-provided repository paths never pass through this service.
/// </summary>
public sealed class ProjectRepositoryStorage(string rootPath)
{
    private readonly string _rootPath = Path.GetFullPath(rootPath);

    public async Task<string> EnsureInitializedAsync(
        string tenantId,
        string projectKey,
        CancellationToken cancellationToken)
    {
        if (!UlidValue.TryParse(tenantId, out _))
            throw new ArgumentException("Tenant ID must be a ULID.", nameof(tenantId));

        var safeKey = projectKey.ToLowerInvariant();
        if (safeKey.Length == 0 ||
            safeKey.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException(
                "Project key must contain only ASCII letters, digits or hyphens.",
                nameof(projectKey));
        }

        var repositoryPath = Path.GetFullPath(Path.Combine(_rootPath, tenantId, safeKey));
        EnsureConfined(repositoryPath);
        if (Directory.Exists(Path.Combine(repositoryPath, ".git")))
            return repositoryPath;
        if (Directory.Exists(repositoryPath) &&
            Directory.EnumerateFileSystemEntries(repositoryPath).Any())
        {
            throw new InvalidOperationException(
                "The managed repository path exists and is not an initialized Git repository.");
        }

        Directory.CreateDirectory(repositoryPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("init");
        startInfo.ArgumentList.Add("--initial-branch");
        startInfo.ArgumentList.Add("main");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        _ = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git could not initialize the managed repository (exit {process.ExitCode}): " +
                error.Trim());
        }

        return repositoryPath;
    }

    private void EnsureConfined(string repositoryPath)
    {
        var relative = Path.GetRelativePath(_rootPath, repositoryPath);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException(
                "Managed repository storage refused a path outside its root.");
        }
    }
}
