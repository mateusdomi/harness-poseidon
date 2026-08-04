using System.Text.Json;
using Harness.Host.WorkBoard;
using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// O executor de gates roda DE VERDADE — é o ponto onde ler o código não basta, porque a correção
/// do OPS-071 só vale se o comando declarado pela entrega realmente executar no host e o resultado
/// realmente chegar ao parecer.
///
/// O teste que sustenta a fronteira é
/// <see cref="TheChildProcessDoesNotInheritTheHostEnvironment"/>: isto executa código que um agente
/// acabou de escrever, e vazar para dentro dele as variáveis que o Host carrega seria o pior modo
/// de falha possível desta correção.
/// </summary>
public sealed class DeliveryGateRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "poseidon-gate-runner-" + Guid.NewGuid().ToString("N"));

    private static bool NpmAvailable =>
        (Environment.GetEnvironmentVariable("PATH")?.Split(':') ?? [])
        .Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"])
        .Any(directory => File.Exists(Path.Combine(directory, "npm")));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Limpeza de temporário nunca deve derrubar o resultado do teste.
        }
    }

    private void WriteManifest(string relativeDirectory, object scripts)
    {
        var directory = Path.Combine(_root, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "package.json"),
            JsonSerializer.Serialize(new { name = "produto", scripts }));
    }

    [Fact]
    public void ManifestsOfThirdPartiesAreNotDeclarationsOfTheDelivery()
    {
        WriteManifest(string.Empty, new { test = "node -e \"\"" });
        WriteManifest(Path.Combine("node_modules", "esbuild"), new { test = "exit 1" });

        var manifests = DeliveryGateRunner.CollectManifests(_root);

        Assert.Single(manifests);
        Assert.Equal(string.Empty, manifests[0].RelativeDirectory);
    }

    [Fact]
    public async Task AGreenScriptAndARedScriptAreReportedAsTheyAre()
    {
        if (!NpmAvailable)
        {
            // Sem runtime no host não há o que provar aqui — e é exatamente o caso que a política
            // reporta como `runtime_unavailable`, coberto por teste puro.
            return;
        }

        WriteManifest(string.Empty, new
        {
            test = "node -e \"process.stdout.write('12 pass')\"",
            build = "node -e \"process.exit(3)\"",
        });

        var plan = DeliveryGateExecutionPolicy.Plan(
            ["test", "build"], DeliveryGateRunner.CollectDeclarations(_root));
        var outcomes = await DeliveryGateRunner.RunAsync(plan, _root, CancellationToken.None);
        var report = DeliveryGateExecutionPolicy.Consolidate(plan, outcomes);

        Assert.Equal(2, outcomes.Count);
        Assert.True(outcomes[0].Passed);
        Assert.Contains("12 pass", outcomes[0].Output, StringComparison.Ordinal);
        Assert.False(outcomes[1].Passed);
        Assert.Equal(3, outcomes[1].ExitCode);
        Assert.Equal(DeliveryGateExecutionPolicy.ReasonFailed, report.ReasonCode);
    }

    [Fact]
    public async Task TheConventionalShellGateOfTheRealDeliveryRuns()
    {
        // Reproduz a forma da PRIMEIRA entrega de código aprovada nesta operação: Python sem
        // dependência de terceiro, gates em tools/backend/*.sh. Antes disto o executor só sabia
        // npm, e a única entrega aprovada teria sido reportada como "não declara como executar".
        var tools = Path.Combine(_root, "tools", "backend");
        Directory.CreateDirectory(tools);
        File.WriteAllText(
            Path.Combine(tools, "test.sh"),
            "#!/usr/bin/env bash\nset -euo pipefail\necho \"==> tests: OK\"\n");
        File.WriteAllText(
            Path.Combine(tools, "build.sh"),
            "#!/usr/bin/env bash\nset -euo pipefail\necho quebrou >&2\nexit 2\n");

        var declarations = DeliveryGateRunner.CollectDeclarations(_root);
        Assert.Equal(2, declarations.Count);
        Assert.All(declarations, d => Assert.Equal(DeliveryGateKind.ShellScript, d.Kind));

        var plan = DeliveryGateExecutionPolicy.Plan(["test", "build"], declarations);
        var outcomes = await DeliveryGateRunner.RunAsync(plan, _root, CancellationToken.None);
        var report = DeliveryGateExecutionPolicy.Consolidate(plan, outcomes);

        Assert.Equal(2, outcomes.Count);
        Assert.True(outcomes[0].Passed);
        Assert.Contains("tests: OK", outcomes[0].Output, StringComparison.Ordinal);
        Assert.False(outcomes[1].Passed);
        Assert.Equal(2, outcomes[1].ExitCode);
        Assert.Contains("quebrou", outcomes[1].Output, StringComparison.Ordinal);
        Assert.Equal(DeliveryGateExecutionPolicy.ReasonFailed, report.ReasonCode);
    }

    [Fact]
    public async Task TheChildProcessDoesNotInheritTheHostEnvironment()
    {
        if (!NpmAvailable)
        {
            // Sem runtime no host não há o que provar aqui — e é exatamente o caso que a política
            // reporta como `runtime_unavailable`, coberto por teste puro.
            return;
        }

        Environment.SetEnvironmentVariable("POSEIDON_GATE_SECRET_PROBE", "vazou");
        try
        {
            WriteManifest(string.Empty, new
            {
                test = "node -e \"process.stdout.write(process.env.POSEIDON_GATE_SECRET_PROBE || 'AUSENTE')\"",
            });

            var plan = DeliveryGateExecutionPolicy.Plan(
                ["test"], DeliveryGateRunner.CollectDeclarations(_root));
            var outcomes = await DeliveryGateRunner.RunAsync(plan, _root, CancellationToken.None);

            Assert.Single(outcomes);
            Assert.Contains("AUSENTE", outcomes[0].Output, StringComparison.Ordinal);
            Assert.DoesNotContain("vazou", outcomes[0].Output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POSEIDON_GATE_SECRET_PROBE", null);
        }
    }
}
