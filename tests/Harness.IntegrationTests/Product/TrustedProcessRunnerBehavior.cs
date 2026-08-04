using Harness.Host.Product;

namespace Harness.IntegrationTests.Product;

/// <summary>
/// O runner que executa código recém-escrito por um agente. Estes testes rodam PROCESSO DE VERDADE
/// — é o ponto em que "o Poseidon verifica" deixa de ser figura de linguagem.
///
/// As três primeiras propriedades são de segurança e valem mais que as de funcionalidade: o modelo
/// não escolhe o executável, não escapa da worktree e não prende a esteira.
/// </summary>
[Collection(ProcessVerificationGroup.Name)]
public sealed class TrustedProcessRunnerBehavior : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poseidon-runner-{Guid.NewGuid():N}");

    public TrustedProcessRunnerBehavior() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ProcessoQuePassaProduzExitZeroESaidaCapturada()
    {
        var result = await new TrustedProcessRunner().RunAsync(
            "git", ["--version"], _root, null, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(VerificationOutcomeKind.Passed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("git", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("git --version", result.CommandLine);
        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task ProcessoQueFalhaProduzExitDiferenteDeZeroENuncaPassa()
    {
        var result = await new TrustedProcessRunner().RunAsync(
            "git", ["cat-file", "-e", "0000000000000000000000000000000000000000"],
            _root, null, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(VerificationOutcomeKind.Failed, result.Outcome);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecutavelForaDaAllowlistNuncaRoda()
    {
        // A trava central: o modelo pode sugerir o que quiser; o que roda é o que o Poseidon
        // reconhece. `bash -c <texto do agente>` nunca chega a nascer.
        var result = await new TrustedProcessRunner().RunAsync(
            "bash", ["-c", "echo comprometido"], _root, null,
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(VerificationOutcomeKind.InfrastructureError, result.Outcome);
        Assert.Contains("allowlist", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(TrustedProcessRunner.IsAllowed("bash"));
        Assert.False(TrustedProcessRunner.IsAllowed("sh"));
        Assert.False(TrustedProcessRunner.IsAllowed("curl"));
        Assert.True(TrustedProcessRunner.IsAllowed("dotnet"));
    }

    [Fact]
    public async Task DiretorioDeTrabalhoQueEscapaDaWorktreeERecusado()
    {
        var result = await new TrustedProcessRunner().RunAsync(
            "git", ["--version"], _root, "../..", TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(VerificationOutcomeKind.InfrastructureError, result.Outcome);
        Assert.Contains("escapa", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CaminhoAbsolutoESymlinkParaForaSaoRecusados()
    {
        Assert.Null(TrustedProcessRunner.ResolveConfined(_root, "/etc"));
        Assert.Null(TrustedProcessRunner.ResolveConfined(_root, "../"));
        Assert.Null(TrustedProcessRunner.ResolveConfined(_root, "nao/existe"));

        var fora = Path.Combine(Path.GetTempPath(), $"poseidon-fora-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fora);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "atalho"), fora);
            Assert.Null(TrustedProcessRunner.ResolveConfined(_root, "atalho"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Sistema sem permissão para criar link: as demais recusas já cobrem a fronteira.
        }
        finally
        {
            Directory.Delete(fora, recursive: true);
        }

        var dentro = Path.Combine(_root, "src");
        Directory.CreateDirectory(dentro);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(dentro)),
            TrustedProcessRunner.ResolveConfined(_root, "src"));
    }

    [Fact]
    public async Task ProcessoQueTravaEstouraOTempoENaoPrendeAEsteira()
    {
        // `git` esperando entrada que nunca chega: o teto de tempo mata a árvore de processos.
        var result = await new TrustedProcessRunner().RunAsync(
            "git", ["hash-object", "--stdin"], _root, null,
            TimeSpan.FromMilliseconds(700), CancellationToken.None);

        // Ou estourou (o esperado), ou terminou sozinho ao ver stdin fechado — o que NÃO pode
        // acontecer é o teste travar, e é isso que este caso protege.
        Assert.NotEqual(VerificationOutcomeKind.InfrastructureError, result.Outcome);
        if (result.Outcome == VerificationOutcomeKind.TimedOut)
        {
            Assert.Equal(-1, result.ExitCode);
        }
    }

    [Fact]
    public async Task ArgumentosSaoTipadosENaoInterpretadosPorShell()
    {
        // Se houvesse shell no meio, o `;` iniciaria outro comando. Como o argumento é tipado,
        // ele chega ao git como um nome de ref absurdo e o processo apenas falha.
        var result = await new TrustedProcessRunner().RunAsync(
            "git", ["rev-parse", "algo; echo comprometido"], _root, null,
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotEqual(VerificationOutcomeKind.Passed, result.Outcome);
        Assert.DoesNotContain("comprometido", result.Output, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
