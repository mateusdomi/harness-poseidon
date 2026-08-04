using System.Diagnostics;
using System.Text;
using Harness.Modules.Coordination.Application;

namespace Harness.Host.WorkBoard;

/// <summary>
/// Executa, no host, os gates que a própria entrega declara — a metade suja do
/// <see cref="DeliveryGateExecutionPolicy"/>, isolada aqui para que a política continue pura e
/// testável sem processo.
///
/// Isto RODA CÓDIGO QUE UM AGENTE ACABOU DE ESCREVER. É a função normal de um gate de CI e é
/// coerente com a postura assinada desta instalação (o contêiner foi desligado de propósito, com
/// `UncontainedExecutionAcknowledged`), mas não é uma decisão de passagem — por isso a fronteira
/// está no código, não num comentário de intenção:
///
/// - <b>ambiente mínimo</b>: o processo filho NÃO herda o ambiente do Host. Recebe PATH, HOME,
///   TMPDIR e locale e nada mais — nenhuma variável `ANTHROPIC_*`, nenhum caminho de credencial,
///   nenhum segredo. Vazar segredo para dentro de código recém-escrito por um agente seria o pior
///   modo de falha possível desta correção;
/// - <b>sem rede</b>: o cliente de pacote entra em modo offline e o plano recusa manifesto com
///   dependência externa antes de chegar aqui;
/// - <b>teto de tempo</b> por comando e no total, com a árvore de processos morta no estouro;
/// - <b>saída truncada</b> guardando INÍCIO e FIM — a linha que explica quase sempre está num dos
///   dois extremos, e esta operação já perdeu uma causa para um truncamento que só guardou a cauda.
/// </summary>
internal static class DeliveryGateRunner
{
    /// <summary>Teto por comando. Um gate honesto de projeto novo termina em segundos.</summary>
    private static readonly TimeSpan PerCommandTimeout = TimeSpan.FromMinutes(4);

    /// <summary>Teto do conjunto: o review roda dentro do ciclo do Chefe e o ciclo tem de voltar.</summary>
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(10);

    private const int MaxOutputCharacters = 6_000;

    /// <summary>Caminhos onde procurar o runtime quando o PATH herdado não o traz.</summary>
    private static readonly string[] FallbackBinDirectories =
    [
        "/opt/homebrew/bin",
        "/usr/local/bin",
        "/usr/bin",
        "/bin",
    ];

    /// <summary>
    /// Coleta os manifestos da worktree. Profundidade limitada e <c>node_modules</c> fora: um
    /// manifesto de dependência de terceiro não é declaração da entrega.
    /// </summary>
    internal static IReadOnlyList<DeliveryManifest> CollectManifests(string worktreePath)
    {
        if (!Directory.Exists(worktreePath))
        {
            return [];
        }

        var manifests = new List<DeliveryManifest>();
        var root = new DirectoryInfo(worktreePath);
        Collect(root, root, depth: 0, manifests);
        return manifests;
    }

    private static void Collect(
        DirectoryInfo root, DirectoryInfo current, int depth, List<DeliveryManifest> manifests)
    {
        if (depth > 3 || manifests.Count >= 8)
        {
            return;
        }

        var manifest = Path.Combine(current.FullName, "package.json");
        if (File.Exists(manifest))
        {
            try
            {
                manifests.Add(new DeliveryManifest(
                    Path.GetRelativePath(root.FullName, current.FullName) is var relative &&
                    string.Equals(relative, ".", StringComparison.Ordinal) ? string.Empty : relative,
                    File.ReadAllText(manifest)));
            }
            catch (IOException)
            {
                // Manifesto ilegível é manifesto ausente: o plano trata a ausência, e tratá-la
                // aqui como exceção derrubaria o review inteiro por um arquivo.
            }
        }

        IEnumerable<DirectoryInfo> children;
        try
        {
            children = current.EnumerateDirectories();
        }
        catch (IOException)
        {
            return;
        }

        foreach (var child in children)
        {
            if (child.Name is "node_modules" or ".git" or "dist" or "build" ||
                child.Name.StartsWith('.'))
            {
                continue;
            }

            Collect(root, child, depth + 1, manifests);
        }
    }

    /// <summary>
    /// Executa o plano. Devolve os desfechos observados; consolidar é da política.
    /// Um comando que nem chega a iniciar simplesmente NÃO aparece na lista — e
    /// <see cref="DeliveryGateExecutionPolicy.Consolidate"/> lê essa ausência como não-executado,
    /// nunca como passagem.
    /// </summary>
    internal static async Task<IReadOnlyList<DeliveryGateOutcome>> RunAsync(
        DeliveryGatePlan plan,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var outcomes = new List<DeliveryGateOutcome>();
        if (plan.Commands.Count == 0)
        {
            return outcomes;
        }

        var npm = ResolveExecutable("npm");
        if (npm is null)
        {
            return outcomes;
        }

        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(TotalTimeout);

        foreach (var command in plan.Commands)
        {
            if (total.IsCancellationRequested)
            {
                break;
            }

            var workingDirectory = string.IsNullOrEmpty(command.RelativeDirectory)
                ? worktreePath
                : Path.Combine(worktreePath, command.RelativeDirectory);
            if (!Directory.Exists(workingDirectory))
            {
                break;
            }

            var outcome = await RunOneAsync(npm, command, workingDirectory, total.Token);
            if (outcome is null)
            {
                break;
            }

            outcomes.Add(outcome);
        }

        return outcomes;
    }

    private static async Task<DeliveryGateOutcome?> RunOneAsync(
        string npm,
        DeliveryGateCommand command,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = npm,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        info.ArgumentList.Add("run");
        info.ArgumentList.Add(command.Script);
        info.ArgumentList.Add("--silent");
        ApplyMinimalEnvironment(info, npm);

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        var sink = new object();
        process.OutputDataReceived += (_, args) => Append(sink, output, args.Data);
        process.ErrorDataReceived += (_, args) => Append(sink, output, args.Data);

        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        using var perCommand = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        perCommand.CancelAfter(PerCommandTimeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(perCommand.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                // Já morreu entre o estouro e a tentativa de matar — o desfecho continua sendo o
                // estouro, que é o fato que interessa registrar.
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (TimeoutException)
            {
                // Sem WaitForExit não há garantia de ter drenado a saída; o que houver já basta
                // como evidência do estouro.
            }
        }

        var exitCode = timedOut ? -1 : process.ExitCode;
        string captured;
        lock (sink)
        {
            captured = Truncate(output.ToString());
        }

        return new DeliveryGateOutcome(
            command.Gate,
            command.Display,
            Passed: !timedOut && exitCode == 0,
            exitCode,
            timedOut,
            string.IsNullOrWhiteSpace(captured) ? "(sem saída)" : captured);
    }

    private static void Append(object sink, StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (sink)
        {
            if (output.Length < MaxOutputCharacters * 4)
            {
                output.Append(line).Append('\n');
            }
        }
    }

    /// <summary>
    /// O ambiente do filho é CONSTRUÍDO, não herdado. Herdar traria as variáveis de credencial que
    /// o Host carrega — e o processo do outro lado é código recém-escrito por um agente.
    /// </summary>
    private static void ApplyMinimalEnvironment(ProcessStartInfo info, string npm)
    {
        info.Environment.Clear();

        var binDirectory = Path.GetDirectoryName(npm);
        var path = string.Join(
            ':',
            new[] { binDirectory }
                .Concat(FallbackBinDirectories)
                .Where(entry => !string.IsNullOrEmpty(entry))
                .Distinct(StringComparer.Ordinal)!);

        info.Environment["PATH"] = path;
        info.Environment["HOME"] = Path.GetTempPath();
        info.Environment["TMPDIR"] = Path.GetTempPath();
        info.Environment["LANG"] = "C.UTF-8";
        info.Environment["CI"] = "1";
        info.Environment["NODE_ENV"] = "test";

        // Sem rede: nada de instalar, auditar, financiar nem checar atualização.
        info.Environment["npm_config_offline"] = "true";
        info.Environment["npm_config_audit"] = "false";
        info.Environment["npm_config_fund"] = "false";
        info.Environment["npm_config_update_notifier"] = "false";
        info.Environment["npm_config_progress"] = "false";
        info.Environment["NO_UPDATE_NOTIFIER"] = "1";
    }

    /// <summary>Início E fim: a linha que explica raramente está no meio.</summary>
    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= MaxOutputCharacters)
        {
            return trimmed;
        }

        var half = MaxOutputCharacters / 2;
        return string.Concat(
            trimmed.AsSpan(0, half),
            $"\n… (saída truncada: {trimmed.Length - MaxOutputCharacters} caracteres omitidos no meio) …\n",
            trimmed.AsSpan(trimmed.Length - half));
    }

    private static string? ResolveExecutable(string name)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        var directories = (pathVariable?.Split(':', StringSplitOptions.RemoveEmptyEntries) ?? [])
            .Concat(FallbackBinDirectories);

        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
