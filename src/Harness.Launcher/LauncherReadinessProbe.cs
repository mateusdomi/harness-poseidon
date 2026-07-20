using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Harness.Launcher;

public sealed record LauncherReadiness(
    string Mode,
    DateTimeOffset ReadyAt,
    IReadOnlyList<string> VerifiedEndpoints);

internal static class LauncherReadinessProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);

    public static async Task<LauncherReadiness> WaitAsync(
        Uri address,
        string runnerToken,
        Process runner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerToken);
        ArgumentNullException.ThrowIfNull(runner);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReadinessTimeout);
        string lastDiagnostic = "nenhum probe concluído";
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                if (runner.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Runner encerrou durante a inicialização (exit code {runner.ExitCode}).");
                }

                return await ProbeOnceAsync(address, runnerToken, deadline.Token);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException or InvalidOperationException or
                TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                lastDiagnostic = exception.Message;
                try
                {
                    await Task.Delay(RetryDelay, deadline.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException(
            $"Readiness operacional excedeu {ReadinessTimeout.TotalSeconds:0} segundos. " +
            $"Último diagnóstico: {lastDiagnostic}");
    }

    private static async Task<LauncherReadiness> ProbeOnceAsync(
        Uri address,
        string runnerToken,
        CancellationToken cancellationToken)
    {
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = cookies,
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = address,
            Timeout = ProbeTimeout,
        };
        var verified = new List<string>();

        await RequireJsonAsync(client, HttpMethod.Get, "/health", HttpStatusCode.OK, cancellationToken);
        verified.Add("GET /health=200");

        using (var shell = await client.GetAsync("/", cancellationToken))
        {
            RequireStatus(shell, HttpStatusCode.OK, "GET /");
            var mediaType = shell.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"GET / retornou Content-Type inesperado: {mediaType ?? "ausente"}.");
            }
        }
        verified.Add("GET /=200 text/html");

        var profiles = await RequireJsonAsync(
            client, HttpMethod.Get, "/api/v1/profiles?limit=1", HttpStatusCode.OK, cancellationToken);
        if (!profiles.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidOperationException("GET /api/v1/profiles não retornou uma página JSON válida.");
        }
        verified.Add("GET /api/v1/profiles=200 JSON");

        string mode;
        if (items.GetArrayLength() == 0)
        {
            await RequireJsonAsync(
                client, HttpMethod.Get, "/api/v1/profiles/current", HttpStatusCode.NotFound,
                cancellationToken);
            await RequireJsonAsync(
                client, HttpMethod.Get, "/api/v1/projects?limit=1", HttpStatusCode.Unauthorized,
                cancellationToken);
            verified.Add("GET /api/v1/profiles/current=404 onboarding");
            verified.Add("GET /api/v1/projects=401 before onboarding");
            mode = "onboarding";
        }
        else
        {
            foreach (var endpoint in new[]
                     {
                         "/api/v1/profiles/current",
                         "/api/v1/organizations?limit=1",
                         "/api/v1/projects?limit=1",
                         "/api/v1/budgets?limit=1",
                         "/api/v1/audit-events?limit=1",
                     })
            {
                await RequireJsonAsync(client, HttpMethod.Get, endpoint, HttpStatusCode.OK, cancellationToken);
                verified.Add($"GET {endpoint.Split('?')[0]}=200 JSON");
            }
            mode = "personal-session";
        }

        await RequireJsonAsync(
            client,
            HttpMethod.Get,
            "/api/v1/event-streams/snapshot?stream=global&afterSequence=0",
            HttpStatusCode.OK,
            cancellationToken);
        verified.Add("GET /api/v1/event-streams/snapshot=200 JSON");

        await RequireJsonAsync(
            client,
            HttpMethod.Post,
            "/hubs/events/negotiate?negotiateVersion=1",
            HttpStatusCode.OK,
            cancellationToken);
        verified.Add("POST /hubs/events/negotiate=200 JSON");

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/_runner/ipc/status"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runnerToken);
            using var response = await client.SendAsync(request, cancellationToken);
            RequireStatus(response, HttpStatusCode.OK, "GET /_runner/ipc/status");
            using var payload = await ParseJsonAsync(response, cancellationToken);
            if (!payload.RootElement.TryGetProperty("status", out var status) ||
                !string.Equals(status.GetString(), "ready", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Runner IPC não confirmou status ready.");
            }
        }
        verified.Add("GET /_runner/ipc/status=200 ready");

        return new LauncherReadiness(mode, DateTimeOffset.UtcNow, verified);
    }

    private static async Task<JsonDocument> RequireJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        HttpStatusCode expected,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        using var response = await client.SendAsync(request, cancellationToken);
        RequireStatus(response, expected, $"{method.Method} {path.Split('?')[0]}");
        return await ParseJsonAsync(response, cancellationToken);
    }

    private static async Task<JsonDocument> ParseJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static void RequireStatus(
        HttpResponseMessage response,
        HttpStatusCode expected,
        string operation)
    {
        if (response.StatusCode != expected)
        {
            throw new InvalidOperationException(
                $"{operation} retornou {(int)response.StatusCode}; esperado {(int)expected}.");
        }
    }
}
