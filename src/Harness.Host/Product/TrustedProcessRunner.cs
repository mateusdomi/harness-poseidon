using System.Diagnostics;
using System.Text;

namespace Harness.Host.Product;

/// <summary>Como uma verificação terminou. Conjunto fechado; ausência nunca é aprovação.</summary>
public enum VerificationOutcomeKind
{
    Passed,
    Failed,
    TimedOut,

    /// <summary>O verificador não pôde rodar (executável ausente, workspace inválido).</summary>
    InfrastructureError,

    /// <summary>O perfil não exige esta verificação.</summary>
    NotApplicable,

    /// <summary>
    /// O perfil EXIGE esta verificação e o Poseidon não sabe produzi-la com segurança nesta entrega.
    /// É diferente de <see cref="NotApplicable"/>: ali o requisito não incide, aqui ele incide e a
    /// plataforma não alcança. Confundir os dois transforma buraco da plataforma em dispensa do
    /// produto, que é a mentira mais cara que este conjunto de tipos pode contar.
    /// </summary>
    NotSupported,
}

/// <summary>
/// O que UMA execução controlada produziu. É a matéria-prima de uma evidência `Verified`: sem
/// comando, código de saída e commit, "o teste passou" volta a ser uma frase.
/// </summary>
public sealed record TrustedProcessResult(
    VerificationOutcomeKind Outcome,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int ExitCode,
    string Output)
{
    public TimeSpan Duration => CompletedAt - StartedAt;

    /// <summary>Linha de comando sanitizada para registro. Nunca contém ambiente nem segredo.</summary>
    public string CommandLine => Arguments.Count == 0
        ? Path.GetFileName(Executable)
        : $"{Path.GetFileName(Executable)} {string.Join(' ', Arguments)}";
}

/// <summary>
/// Executa verificações sobre código que um agente acabou de escrever.
///
/// A postura de segurança é a mesma já assinada em <see cref="WorkBoard.DeliveryGateRunner"/>, da
/// qual este runner é a generalização — e as razões continuam valendo:
///
/// - <b>allowlist de executável</b>: o nome vem de um conjunto FECHADO no código. O modelo pode
///   sugerir o que quiser; o que roda é o que o Poseidon reconhece. Nada de <c>bash -c</c> com
///   texto de agente;
/// - <b>argumentos tipados</b>: cada argumento entra em <c>ArgumentList</c>, nunca numa linha
///   montada por concatenação — metacaractere não escapa do argumento;
/// - <b>confinamento na worktree</b>: o diretório de trabalho é canonicalizado e precisa estar
///   DENTRO da raiz da tentativa. `../`, caminho absoluto e symlink que aponte para fora são
///   recusados antes de qualquer processo nascer;
/// - <b>ambiente construído, não herdado</b>: PATH, HOME, TMPDIR e locale, e nada mais. Vazar uma
///   variável de credencial do Host para dentro de código recém-escrito por um agente seria o pior
///   modo de falha possível;
/// - <b>sem rede</b> no cliente de pacote;
/// - <b>teto de tempo</b> com a árvore de processos morta no estouro;
/// - <b>saída truncada guardando início E fim</b> — a linha que explica quase sempre está num dos
///   dois extremos.
/// </summary>
public sealed class TrustedProcessRunner(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Os únicos executáveis que uma verificação pode invocar. O conjunto é fechado no código de
    /// propósito: é o que impede que "o agente informou o comando" vire execução arbitrária.
    /// </summary>
    private static readonly HashSet<string> Allowlist = new(StringComparer.Ordinal)
    {
        "dotnet", "npm", "npx", "node", "pnpm", "yarn", "git",
    };

    private static readonly string[] FallbackBinDirectories =
        ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin"];

    private const int MaxOutputCharacters = 6_000;

    public static bool IsAllowed(string executable) => Allowlist.Contains(executable);

    /// <summary>
    /// Roda um verificador. <paramref name="workspaceRoot"/> é a fronteira: nada executa fora dela.
    /// </summary>
    public async Task<TrustedProcessResult> RunAsync(
        string executableName,
        IReadOnlyList<string> arguments,
        string workspaceRoot,
        string? relativeWorkingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var startedAt = _time.GetUtcNow();

        if (!IsAllowed(executableName))
        {
            return Infrastructure(
                executableName, arguments, workspaceRoot, startedAt,
                $"Executável '{executableName}' fora da allowlist de verificação.");
        }

        var (info, workingDirectory, error) = Prepare(
            executableName, arguments, workspaceRoot, relativeWorkingDirectory, extraEnvironment);
        if (info is null || workingDirectory is null)
        {
            return Infrastructure(
                executableName, arguments, workingDirectory ?? workspaceRoot, startedAt, error!);
        }

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        var sink = new object();
        process.OutputDataReceived += (_, args) => Append(sink, output, args.Data);
        process.ErrorDataReceived += (_, args) => Append(sink, output, args.Data);

        try
        {
            if (!process.Start())
            {
                return Infrastructure(
                    executableName, arguments, workingDirectory, startedAt, "O processo não iniciou.");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            return Infrastructure(
                executableName, arguments, workingDirectory, startedAt,
                $"Falha ao iniciar o processo: {exception.GetType().Name}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
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
                // Morreu entre o estouro e a tentativa de matar; o estouro continua sendo o fato.
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (TimeoutException)
            {
                // Sem drenar a saída não há garantia de conteúdo; o estouro já é a evidência.
            }
        }

        var exitCode = timedOut ? -1 : process.ExitCode;
        string captured;
        lock (sink)
        {
            captured = Truncate(output.ToString());
        }

        return new TrustedProcessResult(
            timedOut ? VerificationOutcomeKind.TimedOut
                : exitCode == 0 ? VerificationOutcomeKind.Passed : VerificationOutcomeKind.Failed,
            executableName,
            arguments,
            workingDirectory,
            startedAt,
            _time.GetUtcNow(),
            exitCode,
            string.IsNullOrWhiteSpace(captured) ? "(sem saída)" : captured);
    }

    /// <summary>
    /// Inicia um processo de LONGA DURAÇÃO sob as mesmas garantias do
    /// <see cref="RunAsync"/> — allowlist, argumentos tipados, confinamento na worktree, ambiente
    /// construído — e devolve o controle dele ao chamador.
    ///
    /// Existe porque verificar contrato e jornada exige a aplicação NO AR: não dá para buscar o
    /// OpenAPI real de um processo que já terminou. O handle é <see cref="IAsyncDisposable"/> e
    /// mata a árvore de processos no descarte, porque um verificador que deixa órfão transforma a
    /// próxima verificação numa disputa por porta.
    ///
    /// <paramref name="extraEnvironment"/> é POSEIDON-CONTROLADO: quem chama é código deste
    /// repositório, nunca texto de agente. É por onde entram a URL de escuta e a base da API.
    /// </summary>
    public TrustedProcessHandle Start(
        string executableName,
        IReadOnlyList<string> arguments,
        string workspaceRoot,
        string? relativeWorkingDirectory,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var startedAt = _time.GetUtcNow();

        if (!IsAllowed(executableName))
        {
            return TrustedProcessHandle.Failed(
                executableName, arguments, startedAt,
                $"Executável '{executableName}' fora da allowlist de verificação.");
        }

        var (info, _, error) = Prepare(
            executableName, arguments, workspaceRoot, relativeWorkingDirectory, extraEnvironment);
        if (info is null)
        {
            return TrustedProcessHandle.Failed(executableName, arguments, startedAt, error!);
        }

        var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        var sink = new object();
        process.OutputDataReceived += (_, args) => Append(sink, output, args.Data);
        process.ErrorDataReceived += (_, args) => Append(sink, output, args.Data);

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return TrustedProcessHandle.Failed(
                    executableName, arguments, startedAt, "O processo não iniciou.");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            process.Dispose();
            return TrustedProcessHandle.Failed(
                executableName, arguments, startedAt,
                $"Falha ao iniciar o processo: {exception.GetType().Name}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        return new TrustedProcessHandle(process, output, sink, executableName, arguments, startedAt);
    }

    /// <summary>
    /// Monta o <see cref="ProcessStartInfo"/> sob as garantias do runner, ou explica por que não.
    /// Uma implementação só: execução curta e processo longo NÃO podem divergir em confinamento nem
    /// em ambiente, ou a garantia valeria só para metade dos verificadores.
    /// </summary>
    private static (ProcessStartInfo? Info, string? WorkingDirectory, string? Error) Prepare(
        string executableName,
        IReadOnlyList<string> arguments,
        string workspaceRoot,
        string? relativeWorkingDirectory,
        IReadOnlyDictionary<string, string>? extraEnvironment)
    {
        var workingDirectory = ResolveConfined(workspaceRoot, relativeWorkingDirectory);
        if (workingDirectory is null)
        {
            return (null, null,
                $"Diretório de trabalho '{relativeWorkingDirectory}' escapa da worktree da tentativa.");
        }

        var executable = ResolveExecutable(executableName);
        if (executable is null)
        {
            return (null, workingDirectory, $"Executável '{executableName}' não encontrado no host.");
        }

        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        ApplyMinimalEnvironment(info, executable);

        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
            {
                info.Environment[key] = value;
            }
        }

        return (info, workingDirectory, null);
    }

    /// <summary>
    /// Caminho absoluto DENTRO da raiz, ou nulo. Canonicaliza antes de comparar: é o que fecha
    /// `../`, caminho absoluto disfarçado e link simbólico apontando para fora.
    /// </summary>
    internal static string? ResolveConfined(string workspaceRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return null;
        }

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
            if (Directory.Exists(root))
            {
                root = Path.TrimEndingDirectorySeparator(
                    new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? root);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return Directory.Exists(root) ? root : null;
        }

        if (Path.IsPathRooted(relativePath))
        {
            return null;
        }

        string candidate;
        try
        {
            candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(
                root, relativePath.Replace('/', Path.DirectorySeparatorChar))));
            if (Directory.Exists(candidate))
            {
                candidate = Path.TrimEndingDirectorySeparator(
                    new DirectoryInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? candidate);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var inside = candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            string.Equals(candidate, root, StringComparison.Ordinal);
        return inside && Directory.Exists(candidate) ? candidate : null;
    }

    private TrustedProcessResult Infrastructure(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        DateTimeOffset startedAt,
        string reason) =>
        new(VerificationOutcomeKind.InfrastructureError, executable, arguments, workingDirectory,
            startedAt, _time.GetUtcNow(), -1, reason);

    internal static void Append(object sink, StringBuilder output, string? line)
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
    /// o Host carrega — e do outro lado está código recém-escrito por um agente.
    /// </summary>
    private static void ApplyMinimalEnvironment(ProcessStartInfo info, string executable)
    {
        info.Environment.Clear();
        var binDirectory = Path.GetDirectoryName(executable);
        info.Environment["PATH"] = string.Join(
            ':',
            new[] { binDirectory }
                .Concat(FallbackBinDirectories)
                .Where(entry => !string.IsNullOrEmpty(entry))
                .Distinct(StringComparer.Ordinal)!);
        info.Environment["HOME"] = Path.GetTempPath();
        info.Environment["TMPDIR"] = Path.GetTempPath();
        info.Environment["LANG"] = "C.UTF-8";
        info.Environment["CI"] = "1";
        info.Environment["NODE_ENV"] = "test";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        info.Environment["npm_config_offline"] = "true";
        info.Environment["npm_config_audit"] = "false";
        info.Environment["npm_config_fund"] = "false";
        info.Environment["npm_config_update_notifier"] = "false";
        info.Environment["npm_config_progress"] = "false";
        info.Environment["NO_UPDATE_NOTIFIER"] = "1";
    }

    /// <summary>Início E fim: a linha que explica raramente está no meio.</summary>
    internal static string Truncate(string value)
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
        var directories = (Environment.GetEnvironmentVariable("PATH")?
                .Split(':', StringSplitOptions.RemoveEmptyEntries) ?? [])
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

/// <summary>
/// Um processo de verificação VIVO — a aplicação da entrega no ar, sob controle do Poseidon.
///
/// Duas responsabilidades: dar acesso à saída acumulada enquanto ele roda (é assim que se descobre
/// que a aplicação subiu ou morreu) e garantir que ele MORRA, com filhos, no descarte. `dotnet run`
/// nasce como pai de um segundo processo; matar só o pai deixa a porta ocupada e a verificação
/// seguinte falha por um motivo que não é o dela.
/// </summary>
public sealed class TrustedProcessHandle : IAsyncDisposable
{
    private readonly Process? _process;
    private readonly StringBuilder _output;
    private readonly object _sink;
    private bool _disposed;

    internal TrustedProcessHandle(
        Process process,
        StringBuilder output,
        object sink,
        string executable,
        IReadOnlyList<string> arguments,
        DateTimeOffset startedAt)
    {
        _process = process;
        _output = output;
        _sink = sink;
        Executable = executable;
        Arguments = arguments;
        StartedAt = startedAt;
        Started = true;
    }

    private TrustedProcessHandle(
        string executable,
        IReadOnlyList<string> arguments,
        DateTimeOffset startedAt,
        string failureReason)
    {
        _output = new StringBuilder();
        _sink = new object();
        Executable = executable;
        Arguments = arguments;
        StartedAt = startedAt;
        Started = false;
        FailureReason = failureReason;
    }

    internal static TrustedProcessHandle Failed(
        string executable, IReadOnlyList<string> arguments, DateTimeOffset startedAt, string reason) =>
        new(executable, arguments, startedAt, reason);

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>O processo chegou a nascer.</summary>
    public bool Started { get; }

    /// <summary>Por que não nasceu. Nulo quando nasceu.</summary>
    public string? FailureReason { get; }

    public string CommandLine => Arguments.Count == 0
        ? Path.GetFileName(Executable)
        : $"{Path.GetFileName(Executable)} {string.Join(' ', Arguments)}";

    /// <summary>
    /// O processo continua vivo. É verificação obrigatória ao final de uma jornada: um teste que
    /// "passou" com a aplicação morta no meio provou outra coisa.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            if (!Started || _process is null)
            {
                return false;
            }

            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public string Output
    {
        get
        {
            lock (_sink)
            {
                return TrustedProcessRunner.Truncate(_output.ToString());
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or TimeoutException
                or System.ComponentModel.Win32Exception)
        {
            // Já morreu, ou o host não sabe matar a árvore. O descarte não pode lançar: ele roda em
            // `finally` de verificadores, e uma exceção aqui apagaria o resultado real da jornada.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
