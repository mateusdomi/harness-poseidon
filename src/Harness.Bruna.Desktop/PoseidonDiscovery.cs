using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Harness.Bruna.Desktop;

public enum PoseidonStatus
{
    Stopped,
    Starting,
    Healthy,
    Unhealthy,
}

public sealed record PoseidonInstance(
    PoseidonStatus Status,
    Uri? Address,
    int? LauncherPid,
    int? RunnerPid,
    string? Error);

/// <summary>
/// Descobre a instância do Poseidon a partir do arquivo de runtime escrito pelo Launcher
/// e confirma saúde via health check HTTP no loopback.
/// </summary>
public sealed class PoseidonDiscovery(BrunaConfiguration configuration)
    : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _httpClient = new()
    {
        Timeout = HttpTimeout,
    };

    public void Dispose() => _httpClient.Dispose();

    public PoseidonInstance Probe(CancellationToken cancellationToken = default)
    {
        var runtimeFile = Path.Combine(configuration.FilePath, "..", "..", "runtime", "poseidon.json");
        runtimeFile = Path.GetFullPath(runtimeFile);

        if (!File.Exists(runtimeFile))
        {
            return new PoseidonInstance(PoseidonStatus.Stopped, null, null, null, null);
        }

        RuntimeState? state;
        try
        {
            using var stream = File.OpenRead(runtimeFile);
            state = JsonSerializer.Deserialize<RuntimeState>(stream, JsonOptions);
        }
        catch (JsonException exception)
        {
            return new PoseidonInstance(PoseidonStatus.Stopped, null, null, null, exception.Message);
        }

        if (state is null || string.IsNullOrWhiteSpace(state.Address))
        {
            return new PoseidonInstance(PoseidonStatus.Stopped, null, null, null, null);
        }

        if (!Uri.TryCreate(state.Address, UriKind.Absolute, out var address))
        {
            return new PoseidonInstance(PoseidonStatus.Unhealthy, null, state.LauncherPid, state.RunnerPid, "Endereço inválido no estado.");
        }

        if (state.LauncherPid is int launcherPid && !IsProcessAlive(launcherPid))
        {
            return new PoseidonInstance(PoseidonStatus.Stopped, address, launcherPid, state.RunnerPid, "Launcher não está mais executando.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, "/health"));
            using var response = _httpClient.Send(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new PoseidonInstance(PoseidonStatus.Healthy, address, state.LauncherPid, state.RunnerPid, null);
            }

            return new PoseidonInstance(PoseidonStatus.Unhealthy, address, state.LauncherPid, state.RunnerPid, $"HTTP {(int)response.StatusCode}");
        }
        catch (TaskCanceledException)
        {
            return new PoseidonInstance(PoseidonStatus.Starting, address, state.LauncherPid, state.RunnerPid, null);
        }
        catch (HttpRequestException exception)
        {
            return state.LauncherPid is not null
                ? new PoseidonInstance(PoseidonStatus.Starting, address, state.LauncherPid, state.RunnerPid, exception.Message)
                : new PoseidonInstance(PoseidonStatus.Stopped, address, state.LauncherPid, state.RunnerPid, exception.Message);
        }
    }

    public async Task<PoseidonInstance> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var runtimeFile = Path.Combine(configuration.FilePath, "..", "..", "runtime", "poseidon.json");
        runtimeFile = Path.GetFullPath(runtimeFile);

        if (!File.Exists(runtimeFile))
        {
            return new PoseidonInstance(PoseidonStatus.Stopped, null, null, null, null);
        }

        RuntimeState? state;
        try
        {
            using var stream = File.OpenRead(runtimeFile);
            state = await JsonSerializer.DeserializeAsync<RuntimeState>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            return new PoseidonInstance(PoseidonStatus.Stopped, null, null, null, exception.Message);
        }

        if (state is null || string.IsNullOrWhiteSpace(state.Address))
        {
            return new PoseidonInstance(PoseidonStatus.Stopped, null, null, null, null);
        }

        if (!Uri.TryCreate(state.Address, UriKind.Absolute, out var address))
        {
            return new PoseidonInstance(PoseidonStatus.Unhealthy, null, state.LauncherPid, state.RunnerPid, "Endereço inválido no estado.");
        }

        if (state.LauncherPid is int launcherPid && !IsProcessAlive(launcherPid))
        {
            return new PoseidonInstance(PoseidonStatus.Stopped, address, launcherPid, state.RunnerPid, "Launcher não está mais executando.");
        }

        try
        {
            using var response = await _httpClient.GetAsync(new Uri(address, "/health"), cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new PoseidonInstance(PoseidonStatus.Healthy, address, state.LauncherPid, state.RunnerPid, null);
            }

            return new PoseidonInstance(PoseidonStatus.Unhealthy, address, state.LauncherPid, state.RunnerPid, $"HTTP {(int)response.StatusCode}");
        }
        catch (TaskCanceledException)
        {
            return new PoseidonInstance(PoseidonStatus.Starting, address, state.LauncherPid, state.RunnerPid, null);
        }
        catch (HttpRequestException exception)
        {
            return state.LauncherPid is not null
                ? new PoseidonInstance(PoseidonStatus.Starting, address, state.LauncherPid, state.RunnerPid, exception.Message)
                : new PoseidonInstance(PoseidonStatus.Stopped, address, state.LauncherPid, state.RunnerPid, exception.Message);
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed record RuntimeState(
        int SchemaVersion,
        int? LauncherPid,
        int? RunnerPid,
        string Address,
        string DataDirectory,
        string LogsDirectory,
        DateTimeOffset StartedAt,
        string ReadinessMode,
        DateTimeOffset ReadyAt,
        IReadOnlyList<string> VerifiedEndpoints);
}
