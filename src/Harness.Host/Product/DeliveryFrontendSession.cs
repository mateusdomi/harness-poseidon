using System.Net.Http;

namespace Harness.Host.Product;

/// <summary>
/// A interface da entrega SERVINDO, sob controle do Poseidon: numa porta escolhida por ele,
/// apontada para a API por variável que ele define, esperada até responder e morta ao final.
///
/// A base da API entra por variável de ambiente porque é a única forma de o Poseidon saber para
/// onde a tela está falando. Uma entrega que ignore essas variáveis e fixe a URL no código continua
/// funcionando — só não produz a evidência de integração, e a ausência dela é dita em vez de
/// suposta.
/// </summary>
public sealed class DeliveryFrontendSession : IAsyncDisposable
{
    /// <summary>
    /// As convenções de base de API que os ecossistemas de fato usam. Definir todas é barato e
    /// evita reprovar uma entrega correta só porque ela escolheu outro nome legítimo.
    /// </summary>
    private static readonly string[] ApiBaseVariables =
    [
        "VITE_API_URL", "VITE_API_BASE_URL", "VITE_API_BASE", "VITE_BACKEND_URL",
        "REACT_APP_API_URL", "NEXT_PUBLIC_API_URL", "PUBLIC_API_URL", "NG_APP_API_URL",
    ];

    private DeliveryFrontendSession(
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

    public string? BaseUrl { get; }

    public string? Failure { get; }

    public string CommandLine { get; }

    public bool IsUp => Failure is null && BaseUrl is not null && Process is { IsRunning: true };

    public static async Task<DeliveryFrontendSession> StartAsync(
        TrustedProcessRunner runner,
        string workspaceRoot,
        string directory,
        string manifest,
        string? apiBaseUrl,
        TimeSpan readinessTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);

        // `dev` serve direto da fonte e é o que existe em toda entrega Vite; `preview` exige build
        // prévio e nem sempre está declarado. A jornada não muda de significado por causa disso: o
        // que se percorre é a mesma aplicação.
        var script = FrontendLocator.DeclaresScript(manifest, "dev")
            ? "dev"
            : FrontendLocator.DeclaresScript(manifest, "start") ? "start" : null;
        if (script is null)
        {
            return new DeliveryFrontendSession(
                null, null, null,
                $"{directory}/package.json não declara `dev` nem `start`: não há como servir a interface.",
                "npm run dev");
        }

        var port = LocalApplicationProbe.ReserveFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var (manager, arguments) = FrontendLocator.ResolvePackageManager(workspaceRoot, directory, script);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["HOST"] = "127.0.0.1",
            ["BROWSER"] = "none",
        };

        if (!string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            foreach (var variable in ApiBaseVariables)
            {
                environment[variable] = apiBaseUrl;
            }
        }

        var process = runner.Start(
            manager,
            [
                .. arguments,
                "--",
                "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--host", "127.0.0.1",
                "--strictPort",
            ],
            workspaceRoot,
            directory,
            environment);

        if (!process.Started)
        {
            await process.DisposeAsync();
            return new DeliveryFrontendSession(
                null, null, null, process.FailureReason ?? "A interface não iniciou.", process.CommandLine);
        }

        var client = LocalApplicationProbe.CreateClient(TimeSpan.FromSeconds(30));
        var ready = await LocalApplicationProbe.WaitForHttpAsync(
            client, baseUrl + "/", process, readinessTimeout, cancellationToken);
        if (!ready)
        {
            var reason = process.IsRunning
                ? $"A interface não respondeu em {baseUrl} dentro de {readinessTimeout.TotalSeconds:F0}s."
                : $"O servidor da interface morreu durante a subida:\n{process.Output}";
            client.Dispose();
            await process.DisposeAsync();
            return new DeliveryFrontendSession(null, null, null, reason, process.CommandLine);
        }

        return new DeliveryFrontendSession(process, client, baseUrl, null, process.CommandLine);
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
