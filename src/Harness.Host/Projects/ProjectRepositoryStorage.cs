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

    /// <summary>
    /// Raiz dos repositórios que o PRÓPRIO Poseidon cria. O laço do chefe precisa dela para saber
    /// que um projeto nascido aqui é, por definição, controlado — sem isso todo projeto criado
    /// pelo produto ficava fora do alcance da própria fábrica.
    /// </summary>
    public string RootPath => _rootPath;

    /// <summary>
    /// Resolve a raiz de segurança aplicável ao repositório. Projetos criados pelo Poseidon vivem
    /// sob <see cref="RootPath"/>; projetos trazidos pelo dono usam a raiz configurada para
    /// execução externa. Todos os consumidores Git devem usar esta decisão única.
    /// </summary>
    public string ResolveControlledRoot(string repositoryRoot, string configuredControlledRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredControlledRoot);
        var repository = Path.GetFullPath(repositoryRoot);
        return IsUnder(repository, _rootPath)
            ? _rootPath
            : Path.GetFullPath(configuredControlledRoot);
    }

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
        {
            await EnsureInitialCommitAsync(repositoryPath, cancellationToken);
            return repositoryPath;
        }
        if (Directory.Exists(repositoryPath) &&
            Directory.EnumerateFileSystemEntries(repositoryPath).Any())
        {
            throw new InvalidOperationException(
                "The managed repository path exists and is not an initialized Git repository.");
        }

        Directory.CreateDirectory(repositoryPath);
        await RunGitAsync(repositoryPath, cancellationToken,
            "init", "--initial-branch", "main");
        await EnsureInitialCommitAsync(repositoryPath, cancellationToken);

        return repositoryPath;
    }

    /// <summary>
    /// A worktree precisa de uma revisão-base. <c>git init</c> sozinho deixa o repositório sem
    /// <c>HEAD</c>; nesse estado a primeira delegação é aceita, mas falha antes de o profissional
    /// receber o contexto. O commit vazio é deliberado: cria uma base reproduzível sem inventar
    /// arquivos, tecnologia ou arquitetura para o projeto do usuário.
    /// </summary>
    private static async Task EnsureInitialCommitAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var probe = await RunGitAsync(
            repositoryPath,
            allowFailure: true,
            cancellationToken,
            "rev-parse", "--verify", "HEAD");
        if (probe.ExitCode == 0)
        {
            return;
        }

        await RunGitAsync(
            repositoryPath,
            cancellationToken,
            "-c", "user.name=Poseidon",
            "-c", "user.email=poseidon@localhost",
            "commit", "--allow-empty", "--no-gpg-sign", "-m", "chore: initialize project");
    }

    private static Task<GitResult> RunGitAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments) =>
        RunGitAsync(workingDirectory, allowFailure: false, cancellationToken, arguments);

    private static async Task<GitResult> RunGitAsync(
        string workingDirectory,
        bool allowFailure,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;
        if (!allowFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git command failed while preparing the managed repository (exit {process.ExitCode}): " +
                error.Trim());
        }

        return new GitResult(process.ExitCode, output, error);
    }

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);

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

    private static bool IsUnder(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar);
        return path.StartsWith(
            $"{normalizedRoot}{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal);
    }
}
