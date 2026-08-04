using System.Diagnostics;

namespace Harness.Modules.Execution.Infrastructure.Git;

public sealed class GitWorktreeManager : IDisposable
{
    private readonly string _repositoryRoot;
    private readonly string _controlledRoot;
    private readonly SemaphoreSlim _metadataGate = new(1, 1);

    private GitWorktreeManager(string repositoryRoot, string controlledRoot)
    {
        _repositoryRoot = repositoryRoot;
        _controlledRoot = controlledRoot;
    }

    public static async Task<GitWorktreeManager> OpenAsync(
        string repositoryRoot,
        string controlledRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlledRoot);

        var fullControlledRoot = CanonicalizePath(controlledRoot);
        var fullRepositoryRoot = EnsureContained(
            fullControlledRoot, repositoryRoot, nameof(repositoryRoot));

        var result = await RunGitAsync(
            fullRepositoryRoot,
            ["rev-parse", "--show-toplevel"],
            cancellationToken);
        // Comparar o texto de `--show-toplevel` com Path.GetFullPath é incorreto no macOS:
        // `/var/...` e `/private/var/...` podem apontar para o mesmo diretório, e o Git devolve o
        // caminho físico. `--show-prefix` vazio prova diretamente que o working directory é a
        // raiz (e continua recusando uma subpasta), sem depender da grafia do mount/symlink.
        var prefix = await RunGitAsync(
            fullRepositoryRoot,
            ["rev-parse", "--show-prefix"],
            cancellationToken);
        if (result.ExitCode != 0 || prefix.ExitCode != 0 ||
            !string.IsNullOrWhiteSpace(prefix.StandardOutput))
        {
            throw new InvalidOperationException("The configured path is not the root of a Git repository.");
        }

        return new GitWorktreeManager(fullRepositoryRoot, fullControlledRoot);
    }

    public async Task<GitWorktreeDescriptor> CreateTaskWorktreeAsync(
        string branchName,
        string attemptId,
        string worktreePath,
        string baseReference = "HEAD",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseReference);
        if (!branchName.StartsWith("task/", StringComparison.Ordinal) ||
            branchName[0] == '-' ||
            baseReference[0] == '-')
        {
            throw new ArgumentException("Task branches must use the task/ prefix and safe Git references.", nameof(branchName));
        }

        var destination = EnsureContained(_controlledRoot, worktreePath, nameof(worktreePath));

        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            var branchValidation = await RunGitAsync(
                _repositoryRoot,
                ["check-ref-format", "--branch", branchName],
                cancellationToken);
            if (branchValidation.ExitCode != 0)
            {
                throw new ArgumentException("The task branch name is not a valid Git branch.", nameof(branchName));
            }

            var registeredWorktrees = await ListWorktreesCoreAsync(cancellationToken);
            var existing = registeredWorktrees.SingleOrDefault(item =>
                string.Equals(item.WorktreePath, destination, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (!string.Equals(existing.BranchName, branchName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The destination is registered for a different branch.");
                }

                return existing with { AttemptId = attemptId };
            }

            if (Directory.Exists(destination) || File.Exists(destination))
            {
                // Um diretório VAZIO não é trabalho de ninguém: é o ponto de montagem que a
                // sandbox precisa ver antes de o Git criar a worktree (a abertura da sessão
                // acontece antes da preparação para a attestation ter um contêiner vivo).
                // Cheio e não registrado continua proibido — sobrescrever trabalho alheio,
                // não.
                if (File.Exists(destination) ||
                    Directory.EnumerateFileSystemEntries(destination).Any())
                {
                    throw new InvalidOperationException("The worktree destination exists but is not registered by Git.");
                }
            }

            var branchExists = await RunGitAsync(
                _repositoryRoot,
                ["show-ref", "--verify", "--quiet", $"refs/heads/{branchName}"],
                cancellationToken);
            if (branchExists.ExitCode is not (0 or 1))
            {
                throw CreateGitException("inspect the task branch", branchExists);
            }

            var arguments = branchExists.ExitCode == 0
                ? new[] { "worktree", "add", destination, branchName }
                : ["worktree", "add", "-b", branchName, destination, baseReference];
            var creation = await RunGitAsync(_repositoryRoot, arguments, cancellationToken);
            if (creation.ExitCode != 0)
            {
                throw CreateGitException("create the task worktree", creation);
            }

            var created = (await ListWorktreesCoreAsync(cancellationToken)).Single(item =>
                string.Equals(item.WorktreePath, destination, StringComparison.Ordinal));
            return created with { AttemptId = attemptId };
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    /// <summary>
    /// Aplica um patch arquivado dentro de uma worktree controlada, de forma verificada.
    ///
    /// Primeiro faz <c>git apply --check</c>: se o patch não casar com a base atual (arquivos
    /// mudaram desde que ele foi produzido), o patch é STALE e o método devolve
    /// <c>false</c> — nunca força nem aplica parcialmente. Só quando o check passa é que
    /// aplica de verdade. Detectar stale é responsabilidade do chamador, que registra um
    /// achado tipado e segue com o diff anterior apenas como contexto.
    /// </summary>
    public async Task<bool> TryApplyPatchAsync(
        string worktreePath,
        string patchPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(patchPath);
        var destination = EnsureContained(_controlledRoot, worktreePath, nameof(worktreePath));
        var fullPatchPath = Path.GetFullPath(patchPath);
        if (!File.Exists(fullPatchPath))
        {
            throw new FileNotFoundException("The archived patch does not exist.", fullPatchPath);
        }

        var check = await RunGitAsync(
            destination,
            ["apply", "--check", "--whitespace=nowarn", fullPatchPath],
            cancellationToken);
        if (check.ExitCode != 0)
        {
            return false;
        }

        var apply = await RunGitAsync(
            destination,
            ["apply", "--whitespace=nowarn", fullPatchPath],
            cancellationToken);
        if (apply.ExitCode != 0)
        {
            // O check passou mas o apply falhou: trata-se como stale também, sem deixar a
            // worktree meio aplicada.
            return false;
        }

        return true;
    }

    public async Task<IReadOnlyList<string>> ListLocalBranchesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(
            _repositoryRoot,
            ["for-each-ref", "--format=%(refname:short)", "refs/heads"],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw CreateGitException("list local branches", result);
        }

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Resolve uma referência para o commit exato que ela representa. O consumidor grava essa
    /// revisão junto do índice derivado para não recompilar a mesma árvore em todo ciclo.
    /// </summary>
    public async Task<string> ResolveCommitAsync(
        string reference = "HEAD",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (reference[0] == '-')
        {
            throw new ArgumentException("Git references cannot start with '-'.", nameof(reference));
        }

        var result = await RunGitAsync(
            _repositoryRoot,
            ["rev-parse", "--verify", $"{reference}^{{commit}}"],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw CreateGitException("resolve the Git reference", result);
        }

        return result.StandardOutput.Trim();
    }

    public async Task<bool> RemoveTaskWorktreeAsync(
        string branchName,
        string worktreePath,
        bool deleteBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        if (!branchName.StartsWith("task/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Task branches must use the task/ prefix.", nameof(branchName));
        }

        var destination = EnsureContained(_controlledRoot, worktreePath, nameof(worktreePath));
        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            var registered = (await ListWorktreesCoreAsync(cancellationToken)).SingleOrDefault(item =>
                string.Equals(item.WorktreePath, destination, StringComparison.Ordinal));
            if (registered is null)
            {
                if (Directory.Exists(destination) || File.Exists(destination))
                {
                    throw new InvalidOperationException(
                        "Cleanup refused a destination that is not registered as a Git worktree.");
                }

                return false;
            }

            if (!string.Equals(registered.BranchName, branchName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Cleanup refused a worktree registered for another branch.");
            }

            var removal = await RunGitAsync(
                _repositoryRoot,
                ["worktree", "remove", destination],
                cancellationToken);
            if (removal.ExitCode != 0)
            {
                throw CreateGitException("remove the task worktree", removal);
            }

            if (deleteBranch)
            {
                var deletion = await RunGitAsync(
                    _repositoryRoot,
                    ["branch", "--delete", branchName],
                    cancellationToken);
                if (deletion.ExitCode != 0)
                {
                    throw CreateGitException("delete the merged task branch", deletion);
                }
            }

            return true;
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    public async Task<IReadOnlyList<GitWorktreeDescriptor>> ListWorktreesAsync(
        CancellationToken cancellationToken = default)
    {
        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            return await ListWorktreesCoreAsync(cancellationToken);
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    /// <summary>
    /// Colheita governada: commita na branch da tentativa QUALQUER resto não commitado da
    /// worktree (o worker pode terminar sem commitar). Devolve o SHA exato que passa a representar
    /// a entrega, tenha ele sido criado pelo worker ou pela colheita. Nunca destrói trabalho.
    /// </summary>
    public async Task<string> CommitWorktreeLeftoversAsync(
        string worktreePath, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var destination = EnsureContained(_controlledRoot, worktreePath, nameof(worktreePath));

        var status = await RunGitAsync(destination, ["status", "--porcelain"], cancellationToken);
        if (status.ExitCode != 0)
        {
            throw CreateGitException("inspect the worktree status", status);
        }

        if (status.StandardOutput.Trim().Length == 0)
        {
            return await ResolveWorktreeHeadAsync(destination, cancellationToken);
        }

        var add = await RunGitAsync(destination, ["add", "-A"], cancellationToken);
        if (add.ExitCode != 0)
        {
            throw CreateGitException("stage the worktree leftovers", add);
        }

        var commit = await RunGitAsync(
            destination,
            [
                "-c", "user.name=Poseidon Harness", "-c", "user.email=harness@poseidon.local",
                "commit", "--no-verify", "-m", message,
            ],
            cancellationToken);
        if (commit.ExitCode != 0)
        {
            throw CreateGitException("commit the worktree leftovers", commit);
        }

        return await ResolveWorktreeHeadAsync(destination, cancellationToken);
    }

    private static async Task<string> ResolveWorktreeHeadAsync(
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var head = await RunGitAsync(worktreePath, ["rev-parse", "--verify", "HEAD"], cancellationToken);
        if (head.ExitCode != 0)
        {
            throw CreateGitException("resolve the harvested worktree commit", head);
        }

        return head.StandardOutput.Trim();
    }

    /// <summary>
    /// Diff REAL da branch de tentativa contra a base (três pontos: só o que a branch introduziu
    /// desde o merge-base). É o insumo do code review do critic — dado, nunca autoridade.
    /// </summary>
    public async Task<string> DiffBranchAsync(
        string baseReference, string branchName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        if (!branchName.StartsWith("task/", StringComparison.Ordinal) || baseReference[0] == '-')
        {
            throw new ArgumentException("Task branches must use the task/ prefix and safe Git references.", nameof(branchName));
        }

        var diff = await RunGitAsync(
            _repositoryRoot, ["diff", $"{baseReference}...{branchName}"], cancellationToken);
        if (diff.ExitCode != 0)
        {
            throw CreateGitException("diff the task branch", diff);
        }

        return diff.StandardOutput;
    }

    /// <summary>
    /// Os arquivos que a branch da tentativa alterou em relação à referência publicada. É o que
    /// torna o checkpoint TRANSFERÍVEL entre contas: o trabalho parcial vive no Git, não na sessão
    /// do agente que o produziu — então trocar de conta não precisa descartar nada.
    ///
    /// Devolve vazio quando a branch não existe: uma tentativa que morreu antes de commitar não
    /// tem o que transferir, e inventar uma lista aqui criaria a ilusão de retomada.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListBranchChangedFilesAsync(
        string branchName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        if (!branchName.StartsWith("task/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Task branches must use the task/ prefix.", nameof(branchName));
        }

        var head = await RunGitAsync(
            _repositoryRoot, ["rev-parse", "--verify", $"{branchName}^{{commit}}"], cancellationToken);
        if (head.ExitCode != 0)
        {
            return [];
        }

        var names = await RunGitAsync(
            _repositoryRoot, ["diff", "--name-only", $"HEAD...{branchName}"], cancellationToken);
        return names.ExitCode != 0
            ? []
            : [.. names.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)];
    }

    /// <summary>
    /// A branch da tentativa INTRODUZIU alguma mudança sobre a referência publicada?
    ///
    /// Devolve <c>null</c> quando não dá para saber — branch ausente, repositório fora do lugar,
    /// git com erro. "Não sei" é deliberadamente diferente de "não entregou", e por isso não
    /// reaproveita <see cref="ListBranchChangedFilesAsync"/>, que colapsa os dois em lista vazia:
    /// quem chama este método AFROUXA uma guarda quando a resposta é "não entregou", e um erro de
    /// leitura virando permissão é a forma mais cara de errar aqui.
    ///
    /// A colheita governada commita os restos da worktree e devolve o HEAD dela — inclusive quando
    /// nada havia para commitar. Por isso a presença de um <c>git-commit:</c> na evidência da
    /// tentativa NÃO prova entrega: para uma tentativa que não produziu nada, o SHA registrado é o
    /// da própria base. A única prova é o diff.
    /// </summary>
    public async Task<bool?> BranchIntroducedChangesAsync(
        string branchName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        if (!branchName.StartsWith("task/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Task branches must use the task/ prefix.", nameof(branchName));
        }

        var head = await RunGitAsync(
            _repositoryRoot, ["rev-parse", "--verify", $"{branchName}^{{commit}}"], cancellationToken);
        if (head.ExitCode != 0)
        {
            return null;
        }

        var names = await RunGitAsync(
            _repositoryRoot, ["diff", "--name-only", $"HEAD...{branchName}"], cancellationToken);
        return names.ExitCode != 0
            ? null
            : names.StandardOutput.Trim().Length > 0;
    }

    /// <summary>
    /// Lê um artefato diretamente da revisão da branch, sem depender de uma worktree viva. O path
    /// é deliberadamente restrito a <c>docs/</c>: este método alimenta o catálogo documental e
    /// nunca deve virar um leitor genérico de arquivos arbitrários do repositório.
    /// </summary>
    public async Task<string> ReadDocumentFromBranchAsync(
        string branchName, string documentPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        if (!branchName.StartsWith("task/", StringComparison.Ordinal) ||
            Path.IsPathRooted(documentPath) ||
            documentPath.Contains('\\') ||
            !documentPath.StartsWith("docs/", StringComparison.Ordinal) ||
            documentPath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException(
                "Only canonical docs/ paths from task branches can be read.",
                nameof(documentPath));
        }

        var result = await RunGitAsync(
            _repositoryRoot, ["show", $"{branchName}:{documentPath}"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw CreateGitException("read the document artifact from the task branch", result);
        }

        return result.StandardOutput;
    }

    /// <summary>
    /// Integração do gate humano: merge REAL (sempre com commit de merge, --no-ff) da branch de
    /// tentativa aprovada na referência atualmente publicada do repositório. Em conflito, o merge
    /// é abortado e a exceção sobe — nunca deixa o repositório no meio de um merge.
    /// </summary>
    public async Task MergeTaskBranchAsync(
        string branchName, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!branchName.StartsWith("task/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Task branches must use the task/ prefix.", nameof(branchName));
        }

        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            var merge = await RunGitAsync(
                _repositoryRoot,
                [
                    "-c", "user.name=Poseidon Harness", "-c", "user.email=harness@poseidon.local",
                    "merge", "--no-ff", "-m", message, branchName,
                ],
                cancellationToken);
            if (merge.ExitCode != 0)
            {
                _ = await RunGitAsync(_repositoryRoot, ["merge", "--abort"], CancellationToken.None);
                throw CreateGitException("merge the approved task branch", merge);
            }
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    /// <summary>
    /// Registra como ancestral um branch documental aprovado que foi comprovadamente substituído
    /// por uma versão mais nova já publicada. A estratégia <c>ours</c> preserva o conteúdo atual;
    /// este método não decide a supersessão e só pode ser chamado depois da prova no catálogo.
    /// </summary>
    public async Task MergeSupersededTaskBranchAsync(
        string branchName, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!branchName.StartsWith("task/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Task branches must use the task/ prefix.", nameof(branchName));
        }

        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            var merge = await RunGitAsync(
                _repositoryRoot,
                [
                    "-c", "user.name=Poseidon Harness", "-c", "user.email=harness@poseidon.local",
                    "merge", "--no-ff", "-s", "ours", "-m", message, branchName,
                ],
                cancellationToken);
            if (merge.ExitCode != 0)
            {
                _ = await RunGitAsync(_repositoryRoot, ["merge", "--abort"], CancellationToken.None);
                throw CreateGitException("record the superseded approved task branch", merge);
            }
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    public async Task<string> ReadPublishedDocumentAsync(
        string referenceName, string documentPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        if (referenceName[0] == '-' ||
            referenceName.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_' or '.')) ||
            Path.IsPathRooted(documentPath) ||
            documentPath.Contains('\\') ||
            !documentPath.StartsWith("docs/", StringComparison.Ordinal) ||
            documentPath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("The Git reference or document path is invalid.");
        }

        var result = await RunGitAsync(
            _repositoryRoot, ["show", $"{referenceName}:{documentPath}"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw CreateGitException("read the published document", result);
        }

        return result.StandardOutput;
    }

    public void Dispose() => _metadataGate.Dispose();

    private async Task<IReadOnlyList<GitWorktreeDescriptor>> ListWorktreesCoreAsync(
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            _repositoryRoot,
            ["worktree", "list", "--porcelain"],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw CreateGitException("list worktrees", result);
        }

        var descriptors = new List<GitWorktreeDescriptor>();
        string? path = null;
        string? head = null;
        string? branch = null;
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            if (line.Length == 0)
            {
                AddDescriptor(descriptors, path, head, branch);
                path = null;
                head = null;
                branch = null;
            }
            else if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = line[9..];
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = line[5..];
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                branch = line[18..];
            }
        }

        AddDescriptor(descriptors, path, head, branch);
        return descriptors;
    }

    private static void AddDescriptor(
        List<GitWorktreeDescriptor> descriptors,
        string? path,
        string? head,
        string? branch)
    {
        if (path is not null && head is not null && branch is not null)
        {
            descriptors.Add(new GitWorktreeDescriptor(
                string.Empty, branch, CanonicalizePath(path), head));
        }
    }

    private static string EnsureContained(string root, string candidate, string parameterName)
    {
        var fullRoot = CanonicalizePath(root);
        var fullCandidate = CanonicalizePath(candidate);
        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new ArgumentException("The path must be contained by the controlled root.", parameterName);
        }

        return fullCandidate;
    }

    /// <summary>
    /// Normaliza também links em diretórios ancestrais. <c>Path.GetFullPath</c> é apenas lexical:
    /// no macOS ele preserva <c>/var</c>, enquanto Git reporta <c>/private/var</c>. Além de quebrar
    /// a reconciliação de worktrees, comparar caminhos sem resolver ancestrais permitiria que um
    /// symlink já existente atravessasse a raiz controlada.
    /// </summary>
    private static string CanonicalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)
            ?? throw new ArgumentException("The path must have a root.", nameof(path));
        var relative = Path.GetRelativePath(root, full);
        var current = root;
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            if (Directory.Exists(next))
            {
                var target = new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: true);
                current = target?.FullName ?? next;
            }
            else
            {
                current = next;
            }
        }

        return Path.GetFullPath(current);
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/git",
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
        return new GitCommandResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static InvalidOperationException CreateGitException(string operation, GitCommandResult result) =>
        new($"Git could not {operation} (exit {result.ExitCode}): {result.StandardError.Trim()}");

    private sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);
}
