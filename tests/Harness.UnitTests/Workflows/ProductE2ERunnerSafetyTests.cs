using Harness.Host.WorkBoard;
using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Workflows;

public sealed class ProductE2ERunnerSafetyTests
{
    [Fact]
    public void E2EExecutadoComFalhaReprovaOGate()
    {
        var decision = ProductE2EGatePolicy.Decide(
            new ProductE2EResult(Ran: true, Passed: false, "1 failed"));

        Assert.Equal(ProductE2EGateDecision.Failed, decision);
    }

    [Fact]
    public void E2EIndisponivelNaoViraPass()
    {
        var decision = ProductE2EGatePolicy.Decide(
            new ProductE2EResult(Ran: false, Passed: false, "docker indisponível"));

        Assert.Equal(ProductE2EGateDecision.Unavailable, decision);
    }

    [Fact]
    public void PlaceholderDeSecretSemProvedorFalhaFechado()
    {
        var decision = ProductE2EGatePolicy.Decide(
            new ProductE2EResult(
                Ran: false,
                Passed: false,
                "Manifesto E2E referencia variável sem provedor runtime: JWT_SECRET."));

        Assert.Equal(ProductE2EGateDecision.Failed, decision);
    }

    [Fact]
    public void WorktreeSujaFalhaFechado()
    {
        var decision = ProductE2EGatePolicy.Decide(
            new ProductE2EResult(
                Ran: false,
                Passed: false,
                "worktree Git não está limpa; a plataforma não pode associar a prova a um CommitSha imutável."));

        Assert.Equal(ProductE2EGateDecision.Failed, decision);
    }

    [Fact]
    public async Task RunnerRecusaWorktreeSujaAntesDeSubirE2E()
    {
        var root = Directory.CreateTempSubdirectory("poseidon-e2e-dirty-").FullName;
        try
        {
            RunGit(root, "init");
            File.WriteAllText(Path.Combine(root, "arquivo.txt"), "dirty");

            var result = await ProductE2ERunner.RunAsync(
                root,
                new ProductE2EHarness(
                    null,
                    null,
                    null,
                    "/health",
                    "http://127.0.0.1:1",
                    null,
                    ".",
                    ["npm", "test"],
                    new Dictionary<string, string>()),
                CancellationToken.None);

            Assert.False(result.Ran);
            Assert.False(result.Passed);
            Assert.Contains("worktree Git não está limpa", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void SumarioDoPlaywrightExtraiContadoresSemDecidirOGate()
    {
        var summary = ProductE2ERunner.ParsePlaywrightSummary(
            """
              74 passed (1.2m)
              2 skipped
              0 failed
            """);

        Assert.Equal(74, summary.Passed);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(2, summary.Skipped);
    }

    [Fact]
    public void NomeDoComposeEhIsoladoPorExecucao()
    {
        var first = ProductE2ERunner.ComposeProjectName("/tmp/produto");
        var second = ProductE2ERunner.ComposeProjectName("/tmp/produto");

        Assert.StartsWith("poseidon-e2e-", first, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void DetalheDeE2ENaoExpoeSecretsMaterializados()
    {
        const string secret = "SenhaSuperSecreta123!";
        var output = $"falha ao conectar usando Password={secret};User Id=APP";

        var redacted = ProductE2ERunner.RedactedTail(
            output,
            new Dictionary<string, string>
            {
                ["DB_PASSWORD"] = secret,
                ["ConnectionStrings__Default"] = $"User Id=APP;Password={secret}",
            });

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        Assert.True(process.Start());
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
