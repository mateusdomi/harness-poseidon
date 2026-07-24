using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// RN-01 — DISCIPLINA DE CARD. Prova, pela superfície oficial `POST /api/v1/agent-runs`, que
/// nenhum trabalho de agente nasce sem um CARD despachável: um card inexistente é 404 e um card
/// que existe mas NÃO está em estado despachável (bloqueado) é recusado com código tipado ANTES de
/// qualquer claim, conta ou execução. A API não é um atalho que contorna a Definition of Ready.
/// </summary>
public sealed class AgentRunCardDisciplineTests : IDisposable
{
    private readonly string _root;

    public AgentRunCardDisciplineTests()
    {
        var candidate = Path.Combine(
            Path.GetTempPath(), $"harness-card-discipline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(candidate);
        _root = ResolveRealPath(candidate);
    }

    private string ControlledRoot => Path.Combine(_root, "controlled");

    private string RepositoryRoot => Path.Combine(ControlledRoot, "project");

    [Fact]
    public async Task ARunAgainstANonExistentCardIsRefusedAsNotFound()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        CreateRepository();
        await using var app = BuildHost();
        await app.StartAsync(timeout.Token);
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = BaseAddress(app.Services) };
        var projectId = await SeedProjectAsync(client, timeout.Token);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/agent-runs",
            new
            {
                projectId,
                taskId = UlidValue.New(DateTimeOffset.UtcNow).ToString(),
                role = "frontend-specialist",
                account = "worker-codex-frontend",
                instruction = "faça algo",
            },
            timeout.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(
            "task_not_found",
            await response.Content.ReadAsStringAsync(timeout.Token),
            StringComparison.Ordinal);
        await app.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task ARunAgainstABlockedCardIsRefusedAsNotDispatchable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        CreateRepository();
        await using var app = BuildHost();
        await app.StartAsync(timeout.Token);
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = BaseAddress(app.Services) };
        var projectId = await SeedProjectAsync(client, timeout.Token);
        var taskId = await SeedTaskAsync(client, projectId, timeout.Token);

        // O card existe e é 'agent_task' com instrução — despachável. Ao movê-lo para BLOQUEADO,
        // a disciplina de card deve recusar o run.
        using var move = await client.PostAsJsonAsync(
            $"/api/v1/tasks/{taskId}/moves",
            new MoveTaskRequest("blocked", "Aguardando decisão do dono."),
            timeout.Token);
        move.EnsureSuccessStatusCode();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/agent-runs",
            new
            {
                projectId,
                taskId,
                role = "frontend-specialist",
                account = "worker-codex-frontend",
                instruction = "faça algo",
            },
            timeout.Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        Assert.Contains("card_not_dispatchable", body, StringComparison.Ordinal);
        Assert.Contains("dor.blocked", body, StringComparison.Ordinal);
        await app.StopAsync(timeout.Token);
    }

    private WebApplication BuildHost() =>
        HostApplication.Build([
            "--urls", "http://127.0.0.1:0",
            "--Harness:DatabasePath", Path.Combine(_root, "harness.db"),
            "--Harness:AgentRuns:Enabled", "true",
            "--Harness:AgentRuns:ControlledRoot", ControlledRoot,
            "--Harness:AgentRuns:ProfilesRoot", Path.Combine(_root, "profiles"),
        ]);

    private async Task<string> SeedProjectAsync(HttpClient client, CancellationToken token)
    {
        using var profile = await client.PostAsJsonAsync(
            "/api/v1/profiles", new CreateProfileRequest("Operador", null, null, "pt-BR"), token);
        profile.EnsureSuccessStatusCode();
        using var organization = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, token);
        organization.EnsureSuccessStatusCode();
        using var organizationBody = JsonDocument.Parse(await organization.Content.ReadAsStringAsync(token));
        var organizationId = organizationBody.RootElement.GetProperty("id").GetString()!;
        using var project = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = "Poseidon",
                Key = "PSD",
                Description = "Projeto do teste de disciplina de card.",
                RepositoryUrl = RepositoryRoot,
            },
            token);
        project.EnsureSuccessStatusCode();
        using var projectBody = JsonDocument.Parse(await project.Content.ReadAsStringAsync(token));
        return projectBody.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> SeedTaskAsync(HttpClient client, string projectId, CancellationToken token)
    {
        using var task = await client.PostAsJsonAsync(
            "/api/v1/tasks",
            new CreateTaskRequest(projectId, "Documentar o frontend", "Adicione um cabeçalho ao README."),
            token);
        task.EnsureSuccessStatusCode();
        using var taskBody = JsonDocument.Parse(await task.Content.ReadAsStringAsync(token));
        return taskBody.RootElement.GetProperty("id").GetString()!;
    }

    private void CreateRepository()
    {
        Directory.CreateDirectory(Path.Combine(RepositoryRoot, "frontend"));
        File.WriteAllText(Path.Combine(RepositoryRoot, "frontend", "README.md"), "# Frontend\n");
        Git("init --initial-branch=main");
        Git("config user.email operador@example.test");
        Git("config user.name Operador");
        Git("add .");
        Git("commit -m base");
    }

    private void Git(string arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
    }

    private static string ResolveRealPath(string path)
    {
        var startInfo = new ProcessStartInfo("pwd")
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-P");
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output.Length > 0 ? output : path;
    }

    private static Uri BaseAddress(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish an address.");
        return new Uri(addresses.Single(item =>
            item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
