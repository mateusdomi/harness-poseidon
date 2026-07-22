using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// DRIVER do LOOP AUTÔNOMO do chefe, end to end e REAL (opt-in `HARNESS_RUN_CHIEF_LOOP=true`).
/// Liga o auto-dispatch, semeia UM card `Ready` no board (uma demanda backend) e prova que o
/// chefe sozinho: pega o card, escolhe a persona/conta, delega ao agente e o card sai do
/// backlog (`Ready` → despachado). NÃO publica — o card para no resultado.
/// </summary>
public sealed class ChiefLoopPilotDriver
{
    private static string RepoRoot => Environment.GetEnvironmentVariable("HARNESS_PILOT_REPO")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "harness-poseidon-backend");

    private static string ControlledRoot => Path.GetFullPath(Path.Combine(RepoRoot, ".."));
    private static string ProfilesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harness", "accounts");
    private static string AccountsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harness", "agent-accounts.json");
    private static string ResultsDir => Path.Combine(Path.GetTempPath(), "harness-chief-loop");

    [Fact]
    public async Task TheChiefLoopPicksUpACardAndDelegatesItOnItsOwn()
    {
        Directory.CreateDirectory(ResultsDir);
        Log($"driver iniciado; HARNESS_RUN_CHIEF_LOOP='{Environment.GetEnvironmentVariable("HARNESS_RUN_CHIEF_LOOP")}'");
        if (!string.Equals(
                Environment.GetEnvironmentVariable("HARNESS_RUN_CHIEF_LOOP"), "true", StringComparison.Ordinal))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await LoadGlmTokenIntoEnvironmentAsync(timeout.Token);

        await using var app = HostApplication.Build([
            "--urls", "http://127.0.0.1:0",
            "--Harness:DatabasePath", Path.Combine(ResultsDir, "chief.db"),
            "--Harness:AgentRuns:Enabled", "true",
            "--Harness:AgentRuns:ControlledRoot", ControlledRoot,
            "--Harness:AgentRuns:ProfilesRoot", ProfilesRoot,
            "--Harness:AgentRuns:AccountsFilePath", AccountsFile,
            "--Harness:AgentRuns:AutoDispatchEnabled", "true",
            "--Harness:AgentRuns:AutoDispatchInterval", "00:00:05",
            "--Harness:AgentRuns:AutoDispatchMaxConcurrent", "1",
            "--Harness:AgentRuns:RunTimeout", "00:10:00",
            "--Harness:AgentRuns:LeaseDuration", "00:10:00",
        ]);
        await app.StartAsync(timeout.Token);
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = BaseAddress(app.Services), Timeout = TimeSpan.FromMinutes(2) };

        var projectId = await SeedProjectAsync(client, timeout.Token);
        var taskId = await SeedCardAsync(client, projectId, timeout.Token);
        Log($"card semeado: project={projectId} task={taskId}");

        // O loop do chefe (a cada 5s) deve pegar o card, delegar e movê-lo para fora de `Ready`.
        var moved = await WaitUntilDispatchedAsync(client, taskId, timeout.Token);
        Log($"card despachado pelo chefe? {moved}");

        await app.StopAsync(timeout.Token);
        Log("== chief loop driver finished (no publish) ==");
        Assert.True(moved, "O chefe não despachou o card sozinho no tempo esperado.");
    }

    private static async Task<bool> WaitUntilDispatchedAsync(HttpClient client, string taskId, CancellationToken token)
    {
        for (var i = 0; i < 90; i++)
        {
            using var response = await client.GetAsync($"/api/v1/tasks/{taskId}", token);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var state = doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : null;
                if (!string.Equals(state, "Ready", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            await Task.Delay(2000, token);
        }

        return false;
    }

    private static async Task<string> SeedCardAsync(HttpClient client, string projectId, CancellationToken token)
    {
        using var solicitation = await client.PostAsJsonAsync(
            "/api/v1/solicitations",
            new CreateSolicitationRequest(projectId, "request", "Probe do chief loop", "Criar um probe."),
            token);
        solicitation.EnsureSuccessStatusCode();
        var solicitationId = Id(await solicitation.Content.ReadAsStringAsync(token));

        using var demand = await client.PostAsJsonAsync(
            "/api/v1/demands",
            new CreateDemandRequest(projectId, "Probe do chief loop", "Criar o probe.", solicitationId),
            token);
        demand.EnsureSuccessStatusCode();
        var demandId = Id(await demand.Content.ReadAsStringAsync(token));

        using var task = await client.PostAsJsonAsync(
            "/api/v1/tasks",
            new CreateTaskRequest(
                projectId, "Criar o probe do chief loop",
                "Você está numa worktree isolada. Crie EXATAMENTE o arquivo " +
                "`docs/backend/execution/evidence/chief-loop/probe.md` com uma única linha: `chief-ok`. " +
                "Não altere mais nada e finalize.",
                demandId),
            token);
        task.EnsureSuccessStatusCode();
        return Id(await task.Content.ReadAsStringAsync(token));
    }

    private static async Task<string> SeedProjectAsync(HttpClient client, CancellationToken token)
    {
        using var profile = await client.PostAsJsonAsync(
            "/api/v1/profiles", new CreateProfileRequest("Operador", null, null, "pt-BR"), token);
        profile.EnsureSuccessStatusCode();
        using var organization = await client.PostAsJsonAsync(
            "/api/v1/organizations", new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, token);
        organization.EnsureSuccessStatusCode();
        var organizationId = Id(await organization.Content.ReadAsStringAsync(token));
        using var project = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = "Poseidon",
                Key = "PSD",
                Description = "Chief loop end-to-end.",
                RepositoryUrl = Path.GetFullPath(RepoRoot),
            },
            token);
        project.EnsureSuccessStatusCode();
        return Id(await project.Content.ReadAsStringAsync(token));
    }

    private static async Task LoadGlmTokenIntoEnvironmentAsync(CancellationToken token)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN")))
        {
            return;
        }

        var user = Environment.GetEnvironmentVariable("USER") ?? Environment.UserName;
        var psi = new System.Diagnostics.ProcessStartInfo("security")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "find-generic-password", "-s", "poseidon-glm-general", "-a", user, "-w" })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(psi)!;
        var value = (await process.StandardOutput.ReadToEndAsync(token)).Trim();
        await process.WaitForExitAsync(token);
        if (process.ExitCode == 0 && value.Length > 0)
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://api.z.ai/api/anthropic");
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", value);
            Environment.SetEnvironmentVariable("ANTHROPIC_DEFAULT_SONNET_MODEL", "glm-5.2");
        }
    }

    private static string Id(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static Uri BaseAddress(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish an address.");
        return new Uri(addresses.Single(item => item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }

    private static void Log(string message) =>
        File.AppendAllText(
            Path.Combine(ResultsDir, "chief-loop.log"),
            $"[{DateTimeOffset.UtcNow:HH:mm:ss}] {message}{Environment.NewLine}");
}
