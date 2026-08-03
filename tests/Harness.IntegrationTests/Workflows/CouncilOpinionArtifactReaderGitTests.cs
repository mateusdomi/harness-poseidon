using System.Diagnostics;
using Harness.Host.Projects;
using Harness.Host.Workflows;
using Harness.Persistence.Abstractions.Projects;

namespace Harness.IntegrationTests.Workflows;

/// <summary>
/// O conselho consolida DEPOIS que o card fecha, e fechar inclui o merge. Este teste existe por
/// causa dessa ordem: a leitura pela branch (`diff HEAD...branch`) fica VAZIA depois do merge, e
/// era exatamente nesse instante que o parecer precisava ser lido. As duas fontes são exercidas
/// contra git de verdade, antes e depois da integração.
/// </summary>
public sealed class CouncilOpinionArtifactReaderGitTests
{
    [Fact]
    public async Task ParecerIsReadableFromTheTaskBranchBeforeMergeAndFromTheHeadAfterIt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", "council-artifact",
            Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(artifactRoot, "repository");
        const string AttemptId = "01KZ4VT7YAAS8K69VP8EP1H1VN";
        var branch = $"task/agent-run-{AttemptId.ToLowerInvariant()}";
        var parecerPath = GitCouncilOpinionArtifactReader.ParecerPath("playbook-po", 1);
        var body = "# Parecer\n\nVEREDITO: LIBERAR\nRESUMO: o plano cobre o pedido.\n";

        try
        {
            await CreateFixtureRepositoryAsync(repository, timeout.Token);
            await RunGitAsync(repository, ["checkout", "-b", branch], timeout.Token);
            Directory.CreateDirectory(Path.Combine(repository, "docs", "conselho"));
            await File.WriteAllTextAsync(
                Path.Combine(repository, parecerPath), body, timeout.Token);
            await RunGitAsync(repository, ["add", parecerPath], timeout.Token);
            await RunGitAsync(
                repository, ["commit", "-m", "docs(conselho): parecer"], timeout.Token);
            await RunGitAsync(repository, ["checkout", "main"], timeout.Token);

            var project = ProjectWithRepository(repository);
            // A raiz gerenciada aponta para OUTRO lugar de propósito: assim o teste exercita o ramo
            // "projeto trazido pelo dono", que usa a raiz configurada.
            var reader = new GitCouncilOpinionArtifactReader(
                new ProjectRepositoryStorage(Path.Combine(artifactRoot, "managed")), artifactRoot);

            // Antes do merge: o parecer só existe na branch da tentativa.
            var beforeMerge = await reader.ReadAsync(
                project, AttemptId, "playbook-po", 1, timeout.Token);
            Assert.Equal(body, beforeMerge);

            await RunGitAsync(repository, ["merge", "--no-ff", branch, "-m", "merge"], timeout.Token);
            await RunGitAsync(repository, ["branch", "-D", branch], timeout.Token);

            // Depois do merge a branch some e o diff seria vazio: a referência publicada responde.
            var afterMerge = await reader.ReadAsync(
                project, AttemptId, "playbook-po", 1, timeout.Token);
            Assert.Equal(body, afterMerge);

            // Assento que não entregou não vira opinião silenciosa.
            Assert.Null(await reader.ReadAsync(project, AttemptId, "playbook-qa", 1, timeout.Token));

            // O caso que quebrou em produção: repositório CRIADO pelo Poseidon, que vive sob a raiz
            // gerenciada e não sob a configurada. Passar a configurada faz o GitWorktreeManager
            // recusar por contenção — e o conselho perde todas as opiniões em silêncio.
            var managedReader = new GitCouncilOpinionArtifactReader(
                new ProjectRepositoryStorage(artifactRoot),
                Path.Combine(artifactRoot, "raiz-externa-que-nao-contem-o-repo"));
            Assert.Equal(
                body,
                await managedReader.ReadAsync(project, AttemptId, "playbook-po", 1, timeout.Token));
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    private static ProjectRecord ProjectWithRepository(string repository) =>
        new("tenant", "project", "organization", "Projeto", "PROJ", "Objetivo", "active",
            "medium", repository, "local", "main", [], new(null, null, null, null), ["profile"],
            1, "chief", "autonomous", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);

    private static async Task CreateFixtureRepositoryAsync(
        string repositoryPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(repositoryPath);
        await RunGitAsync(repositoryPath, ["init", "--initial-branch=main"], cancellationToken);
        await RunGitAsync(repositoryPath, ["config", "user.name", "Harness Test"], cancellationToken);
        await RunGitAsync(
            repositoryPath, ["config", "user.email", "test@harness.invalid"], cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "README.md"), "fixture\n", cancellationToken);
        await RunGitAsync(repositoryPath, ["add", "README.md"], cancellationToken);
        await RunGitAsync(repositoryPath, ["commit", "-m", "bootstrap"], cancellationToken);
    }

    private static async Task RunGitAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git fixture process did not start.");
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed: {await standardError}");
        }
    }
}
