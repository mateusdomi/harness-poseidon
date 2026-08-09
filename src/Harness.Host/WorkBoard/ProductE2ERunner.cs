using System.Diagnostics;
using System.Text;
using Harness.Modules.Workflows.Product;

namespace Harness.Host.WorkBoard;

public sealed record ProductE2EResult(
    bool Ran,
    bool Passed,
    string Detail,
    string? RunId = null,
    int? PassedCount = null,
    int? FailedCount = null,
    int? SkippedCount = null,
    TimeSpan? Duration = null);

/// <summary>
/// Executa a suíte E2E de NAVEGADOR do produto pela PLATAFORMA, contra o ambiente vivo — a prova
/// que a auto-verificação do ator não entrega (o executor afirmou "E2E verde" com 22 falhas
/// reais; o sandbox dele nem sobe banco). Lê o manifesto declarado
/// <see cref="ProductE2EHarness"/>, sobe banco (compose) + API (dotnet run), instala o browser,
/// roda a suíte, derruba tudo. Qualquer falha de INFRA (não do produto) devolve
/// <c>Ran=false</c> — nunca um falso verde.
///
/// Fronteira: roda no HOST (onde há Docker), fora do sandbox do agente. É a extensão natural do
/// gate de entrega, mas com REDE e ESTADO — porque provar navegador exige app de pé.
/// </summary>
public static class ProductE2ERunner
{
    private static readonly TimeSpan ComposeTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan ApiBootTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan E2ETimeout = TimeSpan.FromMinutes(12);
    private const int MaxOutput = 6_000;

    public static async Task<ProductE2EResult> RunAsync(
        string repositoryRoot,
        ProductE2EHarness harness,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(harness);

        var apiProcess = default(Process);
        var frontProcess = default(Process);
        var attemptedCompose = false;
        var databaseFresh = false;
        var oracleHealthy = false;
        var apiHealthy = false;
        var frontendHealthy = false;
        var playwrightGlobalSetup = "NOT_RUN";
        var startedAt = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");

        var dirty = await DirtyWorktreeAsync(repositoryRoot, cancellationToken);
        if (dirty is { Length: > 0 })
        {
            return new ProductE2EResult(
                false,
                false,
                "worktree Git não está limpa; a plataforma não pode associar a prova a um " +
                $"CommitSha imutável. Primeiro detalhe: {dirty}",
                runId);
        }

        var missingPlaceholders = ProductE2EEnvironment.MissingPlaceholders(harness);
        if (missingPlaceholders.Count > 0)
        {
            return new ProductE2EResult(
                false,
                false,
                "Manifesto E2E referencia variável sem provedor runtime: " +
                string.Join(',', missingPlaceholders) +
                ". Declare em generatedSecrets/dbPortVar; valores secretos não podem ir para Git.",
                runId);
        }

        // AMBIENTE EFÊMERO: gera os segredos declarados e resolve os templates ${VAR} de `env`.
        // É o mesmo env para compose, API e E2E — cada rodada com credenciais próprias, que somem
        // no teardown. Sem isto o compose/API subiam sem senha e o gate devolvia "unavailable".
        var env = ProductE2EEnvironment.Materialize(harness, GenerateSecret, AllocateFreePort);

        // PROJECT NAME ISOLADO POR EXECUÇÃO. Sem `-p`, o docker-compose usa o nome do DIRETÓRIO do
        // compose como project name — e dois produtos com o compose em `infra/` viram ambos o
        // projeto "infra". Nome por execução evita também duas provas simultâneas do MESMO repo
        // compartilharem contêiner/volume por acidente.
        var composeProject = ComposeProjectName(repositoryRoot);
        var diagnostic = () => EnvironmentDiagnostic(
            runId,
            composeProject,
            harness,
            env,
            databaseFresh,
            oracleHealthy,
            apiHealthy,
            frontendHealthy,
            playwrightGlobalSetup);

        try
        {
            // 1. Banco, via o compose DO PRODUTO. Sem Docker, a prova não roda (não reprova).
            if (harness.ComposeFile is { Length: > 0 } compose)
            {
                if (!Execution.ContainerRuntimeProbe.IsAvailable())
                {
                    return new ProductE2EResult(
                        false, false, "Docker indisponível no host: E2E não pôde subir o banco.",
                        runId);
                }

                // Sempre parte de banco LIMPO: a suíte não é idempotente contra Oracle sujo (uma
                // rodada anterior troca as senhas do 1º acesso e a seguinte herda o estado, dando
                // reprovação falsa). `down -v` apaga o volume antes de subir.
                _ = await RunAsync(
                    repositoryRoot, "docker",
                    ["compose", "-p", composeProject, "-f", compose, "down", "-v"],
                    TimeSpan.FromMinutes(3), env, cancellationToken);
                databaseFresh = true;

                attemptedCompose = true;
                var (composeExit, composeOut) = await RunAsync(
                    repositoryRoot, "docker",
                    ["compose", "-p", composeProject, "-f", compose, "up", "-d",
                     .. (harness.ComposeService is { Length: > 0 } svc ? new[] { svc } : [])],
                    ComposeTimeout, env, cancellationToken);
                if (composeExit != 0)
                {
                    return new ProductE2EResult(
                        false, false, $"compose up falhou (exit {composeExit}): {RedactedTail(composeOut, env)}",
                        runId);
                }

                oracleHealthy = await WaitForHealthyAsync(
                    repositoryRoot,
                    composeProject,
                    compose,
                    harness.ComposeService,
                    env,
                    cancellationToken);
                if (!oracleHealthy)
                {
                    return new ProductE2EResult(
                        false, false, diagnostic() + "\nServiço do compose não ficou healthy no tempo previsto.",
                        runId);
                }
            }

            // 2. API, via dotnet run — herda o env efêmero (connection string, jwt, senha).
            if (harness.ApiProject is { Length: > 0 } apiProject)
            {
                apiProcess = StartDetached(
                    repositoryRoot, "dotnet",
                    ["run", "--project", apiProject, "--no-launch-profile", "--", "--urls", harness.ApiUrl],
                    env);
                var healthy = await PollHealthAsync(
                    harness.ApiUrl.TrimEnd('/') + harness.ApiHealthPath, ApiBootTimeout, cancellationToken);
                if (!healthy)
                {
                    return new ProductE2EResult(
                        false, false, diagnostic() + "\nA API do produto não respondeu ao health no tempo previsto.",
                        runId);
                }
                apiHealthy = true;
            }

            // 3. Front, quando a config de Playwright do produto NÃO o sobe sozinha (sem webServer).
            //    Declarado em `frontCommand`; herda o env efêmero (ex.: URL da API).
            if (harness.FrontCommand is { Count: > 0 } frontCommand)
            {
                var frontDir = Path.Combine(repositoryRoot, harness.FrontDir ?? harness.E2eDir);
                frontProcess = StartDetached(frontDir, frontCommand[0], [.. frontCommand.Skip(1)], env);
                if (harness.FrontUrl is { Length: > 0 } frontUrl)
                {
                    var frontReady = await PollHealthAsync(frontUrl, ApiBootTimeout, cancellationToken);
                    if (!frontReady)
                    {
                        return new ProductE2EResult(
                            false, false, diagnostic() + "\nO front do produto não respondeu no tempo previsto.",
                            runId);
                    }
                    frontendHealthy = true;
                }
            }

            // 4. Browser + suíte. A config do produto sobe o próprio front (ou usamos frontCommand).
            var e2eDir = Path.Combine(repositoryRoot, harness.E2eDir);
            var playwrightInstaller = PlaywrightInstallCommand(e2eDir);
            var install = await RunAsync(
                e2eDir, playwrightInstaller.File, playwrightInstaller.Args,
                TimeSpan.FromMinutes(4), env, cancellationToken);
            if (install.ExitCode != 0)
            {
                return new ProductE2EResult(
                    false, false, diagnostic() + $"\nplaywright install falhou: {RedactedTail(install.Output, env)}",
                    runId);
            }

            var e2e = await RunAsync(
                e2eDir, harness.E2eCommand[0], [.. harness.E2eCommand.Skip(1)],
                E2ETimeout, env, cancellationToken);
            var summary = ParsePlaywrightSummary(e2e.Output);
            var duration = DateTimeOffset.UtcNow - startedAt;
            playwrightGlobalSetup = InferPlaywrightGlobalSetup(e2e.Output, e2e.ExitCode);

            // Exit 0 = todas passaram. Exit != 0 com saída de teste = produto reprovou (prova real).
            return new ProductE2EResult(
                true, e2e.ExitCode == 0,
                e2e.ExitCode == 0
                    ? diagnostic() + $"\nE2E verde: {RedactedTail(e2e.Output, env, 400)}"
                    : diagnostic() + $"\nE2E reprovou (exit {e2e.ExitCode}): {RedactedTail(e2e.Output, env)}",
                runId,
                summary.Passed,
                summary.Failed,
                summary.Skipped,
                duration);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ProductE2EResult(
                false, false, diagnostic() + $"\nE2E não executou: {exception.Message}", runId);
        }
        finally
        {
            if (frontProcess is not null)
            {
                try { frontProcess.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                frontProcess.Dispose();
            }

            if (apiProcess is not null)
            {
                try { apiProcess.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                apiProcess.Dispose();
            }

            if (attemptedCompose && harness.ComposeFile is { Length: > 0 } composeDown)
            {
                _ = await RunAsync(
                    repositoryRoot, "docker",
                    ["compose", "-p", composeProject, "-f", composeDown, "down", "-v"],
                    TimeSpan.FromMinutes(3), env, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Gera um valor efêmero do TIPO pedido pelo manifesto. Vale só para uma rodada de gate contra
    /// banco descartável — não é credencial persistida.
    /// <list type="bullet">
    /// <item><c>hex32</c>: 32 bytes em hex (64 chars) — chave de assinatura JWT.</item>
    /// <item><c>policyPassword</c>: forte por construção (maiúscula, minúscula, dígito, símbolo,
    /// 20+ chars) para passar em políticas de senha típicas do produto semeado.</item>
    /// <item>qualquer outro (ex.: <c>password</c>): 24 chars alfanuméricos.</item>
    /// </list>
    /// </summary>
    private static string GenerateSecret(string kind) => kind switch
    {
        "hex32" => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
        "policyPassword" => "Aa1!" + RandomAlphanumeric(20),
        _ => RandomAlphanumeric(24),
    };

    private static (string File, IReadOnlyList<string> Args) PlaywrightInstallCommand(string e2eDir) =>
        File.Exists(Path.Combine(e2eDir, "bun.lock")) ||
        File.Exists(Path.Combine(e2eDir, "bun.lockb"))
            ? ("bunx", ["playwright", "install", "chromium"])
            : ("npx", ["playwright", "install", "chromium"]);

    private static string InferPlaywrightGlobalSetup(string output, int exitCode)
    {
        if (output.Contains("global setup", StringComparison.OrdinalIgnoreCase) &&
            exitCode != 0 &&
            !ParsePlaywrightSummary(output).Failed.HasValue)
        {
            return "FAIL";
        }

        return exitCode == 0 ? "PASS" : "UNKNOWN";
    }

    private static string EnvironmentDiagnostic(
        string runId,
        string composeProject,
        ProductE2EHarness harness,
        IReadOnlyDictionary<string, string> env,
        bool databaseFresh,
        bool oracleHealthy,
        bool apiHealthy,
        bool frontendHealthy,
        string playwrightGlobalSetup)
    {
        static string Status(bool value) => value ? "PASS" : "NOT_CONFIRMED";
        static string Resolved(IReadOnlyDictionary<string, string> env, string key) =>
            env.ContainsKey(key) ? "RESOLVED" : "MISSING";

        var runtimeConfig = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["ADMIN_PASSWORD"] = Resolved(env, "ADMIN_PASSWORD"),
            ["ORACLE_PASSWORD"] = Resolved(env, "ORACLE_PASSWORD"),
        };
        if (harness.GeneratedSecrets is { Count: > 0 })
        {
            foreach (var key in harness.GeneratedSecrets.Keys)
            {
                runtimeConfig[key] = Resolved(env, key);
            }
        }

        var ports = env
            .Where(item => item.Key.EndsWith("PORT", StringComparison.OrdinalIgnoreCase) ||
                           item.Key.Equals("DB_PORT", StringComparison.Ordinal))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key}={item.Value}");

        return string.Join(
            "\n",
            "[poseidon-e2e-environment]",
            $"RunId={runId}",
            $"ComposeProject={composeProject}",
            $"DatabaseFresh={Status(databaseFresh)}",
            "SchemaApplied=INFERRED_FROM_SERVICE_READINESS",
            "SeedExecuted=INFERRED_FROM_PLAYWRIGHT_GLOBAL_SETUP",
            $"OracleHealthy={Status(oracleHealthy)}",
            $"ApiHealthy={Status(apiHealthy)}",
            $"FrontendHealthy={(harness.FrontCommand is { Count: > 0 } ? Status(frontendHealthy) : "MANAGED_BY_PLAYWRIGHT_OR_NOT_DECLARED")}",
            $"RuntimeConfig={string.Join(",", runtimeConfig.Select(item => $"{item.Key}:{item.Value}"))}",
            $"Ports={string.Join(",", ports)}",
            $"PlaywrightGlobalSetup={playwrightGlobalSetup}");
    }

    internal static (int? Passed, int? Failed, int? Skipped) ParsePlaywrightSummary(string output)
    {
        var passed = default(int?);
        var failed = default(int?);
        var skipped = default(int?);
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out var count))
            {
                continue;
            }

            if (string.Equals(parts[1], "passed", StringComparison.Ordinal))
            {
                passed = count;
            }
            else if (string.Equals(parts[1], "failed", StringComparison.Ordinal))
            {
                failed = count;
            }
            else if (string.Equals(parts[1], "skipped", StringComparison.Ordinal))
            {
                skipped = count;
            }
        }

        return (passed, failed, skipped);
    }

    private static string RandomAlphanumeric(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var chars = new char[length];
        for (var index = 0; index < length; index++)
        {
            chars[index] = alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>Nome de projeto docker-compose isolado por execução.</summary>
    internal static string ComposeProjectName(string repositoryRoot)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(repositoryRoot)));
        return "poseidon-e2e-" +
            Convert.ToHexString(hash)[..8].ToLowerInvariant() +
            "-" +
            Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>Acha uma porta TCP livre pedindo a porta 0 ao SO e devolvendo a que ele atribuiu.</summary>
    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<bool> WaitForHealthyAsync(
        string root,
        string composeProject,
        string composeFile,
        string? service,
        IReadOnlyDictionary<string, string> env,
        CancellationToken token)
    {
        if (service is not { Length: > 0 })
        {
            return true;
        }

        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var (psExit, psOutput) = await RunAsync(
                root, "docker",
                ["compose", "-p", composeProject, "-f", composeFile, "ps", "-q", service],
                TimeSpan.FromSeconds(15), env, token);
            var containerId = psOutput.Trim();
            if (psExit != 0 || containerId.Length == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(8), token);
                continue;
            }

            var (inspectExit, inspectOutput) = await RunAsync(
                root, "docker",
                ["inspect", "-f", "{{.State.Health.Status}}", containerId],
                TimeSpan.FromSeconds(15), null, token);
            if (inspectExit == 0 && inspectOutput.Trim().Equals("healthy", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(8), token);
        }

        return false;
    }

    private static async Task<bool> PollHealthAsync(string url, TimeSpan timeout, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var response = await http.GetAsync(url, token);
                if ((int)response.StatusCode < 500)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { }

            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }

        return false;
    }

    private static Process StartDetached(
        string workingDirectory, string file, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env)
    {
        var info = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in env)
        {
            info.Environment[key] = value;
        }

        var process = new Process { StartInfo = info };
        _ = process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string workingDirectory, string file, IReadOnlyList<string> args, TimeSpan timeout,
        IReadOnlyDictionary<string, string>? env, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = file,
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

        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        var buffer = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) buffer.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) buffer.AppendLine(e.Data); };
        _ = process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (124, buffer + $"\n({file} excedeu {timeout.TotalMinutes:0} min)");
        }

        return (process.ExitCode, buffer.ToString());
    }

    private static async Task<string?> DirtyWorktreeAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var (exit, output) = await RunAsync(
            repositoryRoot,
            "git",
            ["status", "--porcelain=v1"],
            TimeSpan.FromSeconds(15),
            null,
            cancellationToken);
        if (exit != 0)
        {
            return "não foi possível executar git status.";
        }

        var first = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return first;
    }

    private static string Tail(string value, int size = MaxOutput) =>
        value.Length <= size ? value : value[^size..];

    internal static string RedactedTail(
        string value,
        IReadOnlyDictionary<string, string> env,
        int size = MaxOutput) =>
        Tail(Redact(value, env), size);

    internal static string Redact(string value, IReadOnlyDictionary<string, string> env)
    {
        var redacted = value;
        foreach (var secret in env.Values
            .Where(item => item.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(item => item.Length))
        {
            redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        return redacted;
    }
}
