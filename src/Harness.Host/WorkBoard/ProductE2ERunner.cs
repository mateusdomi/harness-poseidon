using System.Diagnostics;
using System.Text;
using Harness.Modules.Workflows.Product;

namespace Harness.Host.WorkBoard;

public sealed record ProductE2EResult(bool Ran, bool Passed, string Detail);

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
internal static class ProductE2ERunner
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
        var startedCompose = false;
        try
        {
            // 1. Banco, via o compose DO PRODUTO. Sem Docker, a prova não roda (não reprova).
            if (harness.ComposeFile is { Length: > 0 } compose)
            {
                if (!Execution.ContainerRuntimeProbe.IsAvailable())
                {
                    return new ProductE2EResult(
                        false, false, "Docker indisponível no host: E2E não pôde subir o banco.");
                }

                var (composeExit, composeOut) = await RunAsync(
                    repositoryRoot, "docker",
                    ["compose", "-f", compose, "up", "-d",
                     .. (harness.ComposeService is { Length: > 0 } svc ? new[] { svc } : [])],
                    ComposeTimeout, harness.Env, cancellationToken);
                if (composeExit != 0)
                {
                    return new ProductE2EResult(
                        false, false, $"compose up falhou (exit {composeExit}): {Tail(composeOut)}");
                }

                startedCompose = true;
                await WaitForHealthyAsync(repositoryRoot, harness.ComposeService, cancellationToken);
            }

            // 2. API, via dotnet run — herda o env declarado (connection string, jwt, senha).
            if (harness.ApiProject is { Length: > 0 } apiProject)
            {
                apiProcess = StartDetached(
                    repositoryRoot, "dotnet",
                    ["run", "--project", apiProject, "--no-launch-profile", "--", "--urls", harness.ApiUrl],
                    harness.Env);
                var healthy = await PollHealthAsync(
                    harness.ApiUrl.TrimEnd('/') + harness.ApiHealthPath, ApiBootTimeout, cancellationToken);
                if (!healthy)
                {
                    return new ProductE2EResult(
                        false, false, "A API do produto não respondeu ao health no tempo previsto.");
                }
            }

            // 3. Browser + suíte. A config do produto sobe o próprio front (ou usa FrontUrl).
            var e2eDir = Path.Combine(repositoryRoot, harness.E2eDir);
            var install = await RunAsync(
                e2eDir, "npx", ["playwright", "install", "chromium"],
                TimeSpan.FromMinutes(4), harness.Env, cancellationToken);
            if (install.ExitCode != 0)
            {
                return new ProductE2EResult(
                    false, false, $"playwright install falhou: {Tail(install.Output)}");
            }

            var e2e = await RunAsync(
                e2eDir, harness.E2eCommand[0], [.. harness.E2eCommand.Skip(1)],
                E2ETimeout, harness.Env, cancellationToken);

            // Exit 0 = todas passaram. Exit != 0 com saída de teste = produto reprovou (prova real).
            return new ProductE2EResult(
                true, e2e.ExitCode == 0,
                e2e.ExitCode == 0
                    ? $"E2E verde: {Tail(e2e.Output, 400)}"
                    : $"E2E reprovou (exit {e2e.ExitCode}): {Tail(e2e.Output)}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ProductE2EResult(false, false, $"E2E não executou: {exception.Message}");
        }
        finally
        {
            if (apiProcess is not null)
            {
                try { apiProcess.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                apiProcess.Dispose();
            }

            if (startedCompose && harness.ComposeFile is { Length: > 0 } composeDown)
            {
                _ = await RunAsync(
                    repositoryRoot, "docker",
                    ["compose", "-f", composeDown, "down", "-v"],
                    TimeSpan.FromMinutes(3), harness.Env, CancellationToken.None);
            }
        }
    }

    private static async Task WaitForHealthyAsync(
        string root, string? service, CancellationToken token)
    {
        if (service is not { Length: > 0 })
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var (exit, output) = await RunAsync(
                root, "docker",
                ["inspect", "-f", "{{.State.Health.Status}}", service],
                TimeSpan.FromSeconds(15), null, token);
            if (exit == 0 && output.Trim().Equals("healthy", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(8), token);
        }
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

    private static string Tail(string value, int size = MaxOutput) =>
        value.Length <= size ? value : value[^size..];
}
