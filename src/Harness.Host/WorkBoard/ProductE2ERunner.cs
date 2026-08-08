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
        var frontProcess = default(Process);
        var startedCompose = false;

        // AMBIENTE EFÊMERO: gera os segredos declarados e resolve os templates ${VAR} de `env`.
        // É o mesmo env para compose, API e E2E — cada rodada com credenciais próprias, que somem
        // no teardown. Sem isto o compose/API subiam sem senha e o gate devolvia "unavailable".
        var env = ProductE2EEnvironment.Materialize(harness, GenerateSecret, AllocateFreePort);

        // PROJECT NAME ISOLADO. Sem `-p`, o docker-compose usa o nome do DIRETÓRIO do compose como
        // project name — e dois produtos com o compose em `infra/` viram ambos o projeto "infra".
        // Um `down -v` de um apagava o contêiner do outro (perda de dados observada 2026-08-08). Um
        // nome próprio e único por repositório isola o gate de qualquer ambiente de desenvolvimento.
        var composeProject = ComposeProjectName(repositoryRoot);

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

                // Sempre parte de banco LIMPO: a suíte não é idempotente contra Oracle sujo (uma
                // rodada anterior troca as senhas do 1º acesso e a seguinte herda o estado, dando
                // reprovação falsa). `down -v` apaga o volume antes de subir.
                _ = await RunAsync(
                    repositoryRoot, "docker",
                    ["compose", "-p", composeProject, "-f", compose, "down", "-v"],
                    TimeSpan.FromMinutes(3), env, cancellationToken);

                var (composeExit, composeOut) = await RunAsync(
                    repositoryRoot, "docker",
                    ["compose", "-p", composeProject, "-f", compose, "up", "-d",
                     .. (harness.ComposeService is { Length: > 0 } svc ? new[] { svc } : [])],
                    ComposeTimeout, env, cancellationToken);
                if (composeExit != 0)
                {
                    return new ProductE2EResult(
                        false, false, $"compose up falhou (exit {composeExit}): {Tail(composeOut)}");
                }

                startedCompose = true;
                await WaitForHealthyAsync(repositoryRoot, harness.ComposeService, cancellationToken);
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
                        false, false, "A API do produto não respondeu ao health no tempo previsto.");
                }
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
                            false, false, "O front do produto não respondeu no tempo previsto.");
                    }
                }
            }

            // 4. Browser + suíte. A config do produto sobe o próprio front (ou usamos frontCommand).
            var e2eDir = Path.Combine(repositoryRoot, harness.E2eDir);
            var install = await RunAsync(
                e2eDir, "npx", ["playwright", "install", "chromium"],
                TimeSpan.FromMinutes(4), env, cancellationToken);
            if (install.ExitCode != 0)
            {
                return new ProductE2EResult(
                    false, false, $"playwright install falhou: {Tail(install.Output)}");
            }

            var e2e = await RunAsync(
                e2eDir, harness.E2eCommand[0], [.. harness.E2eCommand.Skip(1)],
                E2ETimeout, env, cancellationToken);

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

            if (startedCompose && harness.ComposeFile is { Length: > 0 } composeDown)
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

    /// <summary>
    /// Nome de projeto docker-compose ISOLADO e estável por repositório: <c>poseidon-e2e-</c> +
    /// hash curto do caminho. Não colide com o project name padrão (nome do diretório do compose,
    /// que para dois produtos em <c>infra/</c> seria o mesmo "infra") nem com ambientes de dev.
    /// </summary>
    private static string ComposeProjectName(string repositoryRoot)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(repositoryRoot)));
        return "poseidon-e2e-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
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
