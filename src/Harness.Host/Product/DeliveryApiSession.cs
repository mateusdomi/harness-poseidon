using System.Net.Http;

namespace Harness.Host.Product;

/// <summary>
/// A aplicação da entrega NO AR, sob controle do Poseidon: compilada, iniciada numa porta que o
/// Poseidon escolheu, esperada até responder e morta ao final com toda a árvore de processos.
///
/// Existe como tipo próprio porque três verificações precisam exatamente disto e por motivos
/// diferentes — buscar o contrato real, exercitar a jornada e provar que o dado sobrevive. Duplicar
/// a subida em cada uma produziria três semânticas de "está pronto", e a mais frouxa venceria.
/// </summary>
public sealed class DeliveryApiSession : IAsyncDisposable
{
    private DeliveryApiSession(
        TrustedProcessHandle? process, HttpClient? client, string? baseUrl, string? failure, string commandLine)
    {
        Process = process;
        Client = client;
        BaseUrl = baseUrl;
        Failure = failure;
        CommandLine = commandLine;
    }

    public TrustedProcessHandle? Process { get; }

    public HttpClient? Client { get; }

    /// <summary>Base sem barra final, ex.: <c>http://127.0.0.1:51234</c>. Nulo quando não subiu.</summary>
    public string? BaseUrl { get; }

    /// <summary>Por que não subiu. Nulo quando subiu.</summary>
    public string? Failure { get; }

    public string CommandLine { get; }

    public bool IsUp => Failure is null && BaseUrl is not null && Process is { IsRunning: true };

    /// <summary>
    /// Compila e sobe a aplicação. O build entra aqui de propósito: subir sem compilar esconderia
    /// um binário velho de outro commit e a evidência falaria de código que não é o entregue.
    /// </summary>
    public static async Task<DeliveryApiSession> StartAsync(
        TrustedProcessRunner runner,
        string workspaceRoot,
        string projectRelativePath,
        TimeSpan readinessTimeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(runner);

        var build = await runner.RunAsync(
            "dotnet", ["build", projectRelativePath, "-c", "Release", "--nologo"],
            workspaceRoot, null, TimeSpan.FromMinutes(10), cancellationToken);
        if (build.Outcome != VerificationOutcomeKind.Passed)
        {
            return new DeliveryApiSession(
                null, null, null,
                $"A aplicação não compilou, então não há o que subir: {build.CommandLine} → " +
                $"exit {build.ExitCode}.",
                build.CommandLine);
        }

        var port = LocalApplicationProbe.ReserveFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_URLS"] = baseUrl,
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
        };

        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
            {
                environment[key] = value;
            }
        }

        var process = runner.Start(
            "dotnet",
            ["run", "--project", projectRelativePath, "-c", "Release", "--no-build", "--urls", baseUrl],
            workspaceRoot,
            null,
            environment);

        if (!process.Started)
        {
            await process.DisposeAsync();
            return new DeliveryApiSession(
                null, null, null, process.FailureReason ?? "A aplicação não iniciou.", process.CommandLine);
        }

        var client = LocalApplicationProbe.CreateClient(TimeSpan.FromSeconds(30));
        var ready = await LocalApplicationProbe.WaitForHttpAsync(
            client, baseUrl + "/", process, readinessTimeout, cancellationToken);
        if (!ready)
        {
            var reason = process.IsRunning
                ? $"A aplicação não respondeu em {baseUrl} dentro de " +
                  $"{readinessTimeout.TotalSeconds:F0}s."
                : $"A aplicação morreu durante a subida:\n{process.Output}";
            client.Dispose();
            await process.DisposeAsync();
            return new DeliveryApiSession(null, null, null, reason, process.CommandLine);
        }

        return new DeliveryApiSession(process, client, baseUrl, null, process.CommandLine);
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (Process is not null)
        {
            await Process.DisposeAsync();
        }
    }
}
