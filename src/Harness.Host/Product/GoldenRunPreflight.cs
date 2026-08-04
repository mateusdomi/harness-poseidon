using System.Globalization;
using System.Net;
using System.Text;

namespace Harness.Host.Product;

/// <summary>Como uma checagem de prontidão terminou.</summary>
public enum PreflightStatus
{
    /// <summary>O recurso existe e funciona.</summary>
    Ready,

    /// <summary>Não existe. O Golden Run daquele perfil não pode começar.</summary>
    Missing,

    /// <summary>Existe e não pôde ser exercitado nesta máquina.</summary>
    Degraded,
}

/// <param name="Detail">O que foi encontrado — versão, caminho, contagem. Nunca só "ok".</param>
public sealed record PreflightCheck(string Name, PreflightStatus Status, string Detail);

/// <summary>
/// O veredito da instalação. <see cref="Ready"/> só é verdadeiro quando NENHUMA checagem essencial
/// está ausente — degradação declarada não impede começar, ausência impede.
/// </summary>
public sealed record GoldenRunPreflightReport(IReadOnlyList<PreflightCheck> Checks)
{
    public bool Ready => Checks.All(check => check.Status != PreflightStatus.Missing);

    /// <summary>Os motivos, em código estável, do que falta. Vazio quando está pronto.</summary>
    public IReadOnlyList<string> MissingReasons =>
        [.. Checks.Where(check => check.Status == PreflightStatus.Missing)
            .Select(check => $"{check.Name}_missing")];

    public string Summary() => Ready
        ? "golden_run_preflight:ready"
        : "golden_run_preflight:not_ready:" + string.Join(',', MissingReasons);
}

/// <summary>
/// Descobre ANTES do Golden Run se ESTA instalação consegue executar o caminho feliz da
/// verificação — em vez de descobrir no meio de uma execução real que o navegador nunca esteve lá.
///
/// A regra que molda o desenho, e que vem do §C3: <b>código de projeto produzido por agente
/// permanece offline</b>. Nada aqui baixa nada, e em particular nada roda
/// <c>npx playwright install</c> a partir da worktree durante a verificação. Runtime ausente vira
/// diagnóstico com nome — <c>playwright_runtime_missing</c> —, nunca uma instalação silenciosa
/// disparada por trabalho de agente.
///
/// O smoke do navegador é POSEIDON-OWNED de ponta a ponta: página servida por este processo,
/// script escrito por este código, num diretório deste código. Ele fecha exatamente uma pergunta —
/// "o navegador abre e navega nesta máquina?" — e nenhuma outra. Não substitui o Golden Run.
/// </summary>
public sealed class GoldenRunPreflight(TrustedProcessRunner runner)
{
    /// <summary>Marcador que a página de teste carrega e que o navegador precisa devolver.</summary>
    internal const string SmokeMarker = "poseidon-browser-smoke-ok";

    public async Task<GoldenRunPreflightReport> InspectAsync(
        string repositoryRoot,
        ProductVerificationRunner verifiers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifiers);

        var checks = new List<PreflightCheck>
        {
            Executable("dotnet"),
            Executable("git"),
            Executable("node"),
            Executable("npm"),
            Executable("npx"),
            Ports(),
            Workspace(repositoryRoot),
            Verifiers(verifiers),
        };

        var runtime = PlaywrightRuntime(repositoryRoot);
        checks.Add(runtime.Check);

        var browsers = Browsers();
        checks.Add(browsers.Check);

        checks.Add(runtime.ModuleRoot is not null && browsers.Path is not null
            ? await SmokeAsync(runtime.ModuleRoot, browsers.Path, cancellationToken)
            : new PreflightCheck(
                "browser_smoke",
                PreflightStatus.Missing,
                "não executado: o runtime do Playwright ou os navegadores não estão provisionados " +
                "nesta instalação. Provisionar é operação de MÁQUINA, feita fora da verificação " +
                "(`npm ci` no frontend do Poseidon e `npx playwright install`), nunca disparada a " +
                "partir da worktree de um agente."));

        return new GoldenRunPreflightReport(checks);
    }

    private static PreflightCheck Executable(string name)
    {
        var allowed = TrustedProcessRunner.IsAllowed(name);
        if (!allowed)
        {
            return new PreflightCheck(name, PreflightStatus.Missing, "fora da allowlist de verificação");
        }

        var found = ResolveOnPath(name);
        return found is null
            ? new PreflightCheck(name, PreflightStatus.Missing, "não encontrado no PATH do host")
            : new PreflightCheck(name, PreflightStatus.Ready, found);
    }

    /// <summary>
    /// Uma porta livre agora é uma porta livre depois — a corrida existe e é aceitável, mas não
    /// conseguir reservar NENHUMA é impedimento absoluto: toda verificação que sobe aplicação
    /// depende disto.
    /// </summary>
    private static PreflightCheck Ports()
    {
        try
        {
            var first = LocalApplicationProbe.ReserveFreePort();
            var second = LocalApplicationProbe.ReserveFreePort();
            return new PreflightCheck(
                "loopback_ports",
                PreflightStatus.Ready,
                $"portas de loopback disponíveis (ex.: {first}, {second})");
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException)
        {
            return new PreflightCheck(
                "loopback_ports", PreflightStatus.Missing, "não foi possível reservar porta local");
        }
    }

    private static PreflightCheck Workspace(string repositoryRoot)
    {
        if (!Directory.Exists(repositoryRoot))
        {
            return new PreflightCheck("workspace", PreflightStatus.Missing, $"{repositoryRoot} não existe");
        }

        var probe = Path.Combine(repositoryRoot, $".poseidon-preflight-{Environment.ProcessId}");
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new PreflightCheck("workspace", PreflightStatus.Ready, $"{repositoryRoot} é gravável");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new PreflightCheck(
                "workspace", PreflightStatus.Missing, $"{repositoryRoot} não é gravável");
        }
    }

    /// <summary>
    /// O registro de verificadores é parte da prontidão: um Golden Run com um requisito sem
    /// provedor descobriria o buraco no meio, e o diagnóstico seria sobre o produto quando o
    /// problema é da plataforma.
    /// </summary>
    private static PreflightCheck Verifiers(ProductVerificationRunner verifiers)
    {
        var essentials = new[]
        {
            Modules.Workflows.Product.ProductEvidenceKind.BackendBuild,
            Modules.Workflows.Product.ProductEvidenceKind.AutomatedTestsPassed,
            Modules.Workflows.Product.ProductEvidenceKind.OpenApiGenerated,
            Modules.Workflows.Product.ProductEvidenceKind.PersistenceVerified,
            Modules.Workflows.Product.ProductEvidenceKind.E2EJourneyPassed,
            Modules.Workflows.Product.ProductEvidenceKind.SecurityScanPassed,
        };

        var missing = essentials.Where(kind => !verifiers.NativeVerifiers.ContainsKey(kind)).ToArray();
        return missing.Length == 0
            ? new PreflightCheck(
                "native_verifiers",
                PreflightStatus.Ready,
                $"{verifiers.NativeVerifiers.Count} tipos com verificador nativo registrado")
            : new PreflightCheck(
                "native_verifiers",
                PreflightStatus.Missing,
                $"sem verificador nativo para: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// O runtime do Playwright do PRÓPRIO Poseidon — nunca o da entrega. A entrega roda offline; o
    /// que dá para exercitar antes do Golden Run é o que já está instalado nesta máquina.
    /// </summary>
    internal static (PreflightCheck Check, string? ModuleRoot) PlaywrightRuntime(string repositoryRoot)
    {
        foreach (var candidate in (string[])["frontend/node_modules", "node_modules"])
        {
            var modules = Path.Combine(
                repositoryRoot, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(Path.Combine(modules, "playwright")) ||
                Directory.Exists(Path.Combine(modules, "@playwright")))
            {
                return (
                    new PreflightCheck("playwright_runtime", PreflightStatus.Ready, modules),
                    modules);
            }
        }

        return (
            new PreflightCheck(
                "playwright_runtime",
                PreflightStatus.Missing,
                "nenhum `node_modules` do Poseidon contém o Playwright"),
            null);
    }

    /// <summary>Os executáveis de navegador já baixados. Provisionar é operação de máquina.</summary>
    internal static (PreflightCheck Check, string? Path) Browsers()
    {
        var configured = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(home, "Library", "Caches", "ms-playwright"));
        candidates.Add(Path.Combine(home, ".cache", "ms-playwright"));

        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var chromium = Directory.EnumerateDirectories(candidate, "chromium-*")
                .Concat(Directory.EnumerateDirectories(candidate, "chromium_headless_shell-*"))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (chromium.Length > 0)
            {
                return (
                    new PreflightCheck(
                        "browser_executable",
                        PreflightStatus.Ready,
                        $"{chromium.Length} build(s) de chromium em {candidate} " +
                        $"(mais recente: {Path.GetFileName(chromium[^1])})"),
                    candidate);
            }
        }

        return (
            new PreflightCheck(
                "browser_executable",
                PreflightStatus.Missing,
                "nenhum executável de navegador provisionado"),
            null);
    }

    /// <summary>
    /// O smoke: página servida por ESTE processo, script escrito por ESTE código, navegador aberto
    /// e fechado. Fecha uma pergunta e só ela — o runtime de navegador funciona nesta máquina.
    /// </summary>
    private async Task<PreflightCheck> SmokeAsync(
        string moduleRoot, string browsersPath, CancellationToken cancellationToken)
    {
        var workspace = Path.Combine(
            Path.GetTempPath(), $"poseidon-browser-smoke-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}");
        Directory.CreateDirectory(workspace);

        HttpListener? listener = null;
        try
        {
            var port = LocalApplicationProbe.ReserveFreePort();
            var url = $"http://127.0.0.1:{port}";
            listener = new HttpListener();
            listener.Prefixes.Add(url + "/");
            listener.Start();
            var serving = ServeAsync(listener);

            File.WriteAllText(Path.Combine(workspace, "smoke.js"), SmokeScript(url));

            var result = await runner.RunAsync(
                "node", ["smoke.js"], workspace, null, TimeSpan.FromMinutes(3), cancellationToken,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["NODE_PATH"] = moduleRoot,
                    ["PLAYWRIGHT_BROWSERS_PATH"] = browsersPath,

                    // Sem isto o Playwright tenta baixar o que faltar. Aqui ele precisa FALHAR se
                    // faltar, porque baixar durante uma verificação é exatamente o que o §C3 proíbe.
                    ["PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD"] = "1",
                });

            await serving.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None)
                .ContinueWith(_ => { }, TaskScheduler.Default);

            return result.Outcome == VerificationOutcomeKind.Passed &&
                result.Output.Contains(SmokeMarker, StringComparison.Ordinal)
                ? new PreflightCheck(
                    "browser_smoke",
                    PreflightStatus.Ready,
                    $"navegador abriu {url}, leu a página e fechou sem rede")
                : new PreflightCheck(
                    "browser_smoke",
                    PreflightStatus.Degraded,
                    $"o navegador não completou a navegação: {result.CommandLine} → " +
                    $"exit {result.ExitCode}\n{result.Output}");
        }
        catch (Exception exception) when (
            exception is HttpListenerException or IOException or UnauthorizedAccessException)
        {
            return new PreflightCheck(
                "browser_smoke", PreflightStatus.Degraded,
                $"não foi possível montar o smoke: {exception.GetType().Name}");
        }
        finally
        {
            try
            {
                listener?.Close();
                if (Directory.Exists(workspace))
                {
                    Directory.Delete(workspace, recursive: true);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // Sobra de diretório temporário não invalida o resultado do smoke.
            }
        }
    }

    private static async Task ServeAsync(HttpListener listener)
    {
        var page = Encoding.UTF8.GetBytes(
            $"<!doctype html><html><body><h1 id=\"marcador\">{SmokeMarker}</h1></body></html>");

        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception exception) when (
                exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(page);
                context.Response.Close();
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException or HttpListenerException)
            {
                return;
            }
        }
    }

    /// <summary>O script do smoke. Escrito por este código, nunca vindo de entrega nem de modelo.</summary>
    private static string SmokeScript(string url) =>
        string.Join(
            '\n',
            "const { chromium } = require('playwright');",
            "(async () => {",
            "  const browser = await chromium.launch();",
            "  try {",
            "    const page = await browser.newPage();",
            $"    await page.goto('{url}/', {{ waitUntil: 'load', timeout: 30000 }});",
            "    const texto = await page.textContent('#marcador');",
            $"    if (texto !== '{SmokeMarker}') {{",
            "      console.error('marcador inesperado: ' + texto);",
            "      process.exit(2);",
            "    }",
            "    console.log(texto);",
            "  } finally {",
            "    await browser.close();",
            "  }",
            "})().catch((erro) => { console.error(String(erro)); process.exit(3); });");

    private static string? ResolveOnPath(string name)
    {
        var directories = (Environment.GetEnvironmentVariable("PATH")?
                .Split(':', StringSplitOptions.RemoveEmptyEntries) ?? [])
            .Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin"]);

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
