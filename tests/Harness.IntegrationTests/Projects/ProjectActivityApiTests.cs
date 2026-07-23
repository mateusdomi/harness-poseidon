using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Harness.IntegrationTests.Support;

namespace Harness.IntegrationTests.Projects;

/// <summary>
/// CAT-07: o endpoint de atividade recente devolve eventos DERIVADOS de dados duráveis (demandas,
/// tarefas, solicitações), do mais recente para o mais antigo, paginado por cursor e escopado por
/// tenant. Nada é inventado: cada item corresponde a uma linha realmente gravada.
/// </summary>
public sealed class ProjectActivityApiTests
{
    [Fact]
    public async Task ActivityIsMostRecentFirstPaginatedAndTenantScoped()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"cat07-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "cat07.db");
        Directory.CreateDirectory(root);
        var cookies = new CookieContainer();

        try
        {
            await using var app = CreateHost(database);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };

                var profile = await CreateProfileAsync(client, timeout.Token);
                await ProviderCatalogTestSeed.SeedForLocalProfileAsync(app.Services, timeout.Token);
                var organization = await CreateOrganizationAsync(client, timeout.Token);
                var project = await CreateProjectAsync(client, organization.Id, timeout.Token);

                // Duas cadeias de trabalho em instantes distintos: a mais nova deve vir primeiro.
                var older = DateTimeOffset.UtcNow.AddMinutes(-10);
                var newer = DateTimeOffset.UtcNow.AddMinutes(-1);
                await SeedChainAsync(app.Services, profile.Id, project.Id, "Older demand", older, timeout.Token);
                await SeedChainAsync(app.Services, profile.Id, project.Id, "Newer demand", newer, timeout.Token);

                var full = await client.GetFromJsonAsync<ProjectActivityPage>(
                    $"/api/v1/projects/{project.Id}/activity", timeout.Token);
                Assert.NotNull(full);
                Assert.Equal(project.Id, full.ProjectId);
                // Duas cadeias = 2 solicitações + 2 demandas + 2 tarefas = 6 eventos.
                Assert.Equal(6, full.Count);
                Assert.Null(full.NextCursor);

                // Estritamente do mais recente para o mais antigo.
                for (var i = 1; i < full.Items.Count; i++)
                {
                    Assert.True(full.Items[i - 1].OccurredAt >= full.Items[i].OccurredAt);
                }

                // Os três primeiros eventos pertencem à cadeia mais nova; os três últimos à mais antiga.
                Assert.All(full.Items.Take(3), item => Assert.True(item.OccurredAt >= newer));
                Assert.All(full.Items.Skip(3), item => Assert.True(item.OccurredAt <= older.AddSeconds(1)));

                var kinds = full.Items.Select(item => item.Kind).ToHashSet();
                Assert.Contains("demand_created", kinds);
                Assert.Contains("task_created", kinds);
                Assert.Contains("solicitation_created", kinds);
                Assert.All(full.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.Summary)));

                // Paginação por cursor: sem sobreposição e sem lacuna, cobrindo os 6 eventos.
                var first = await client.GetFromJsonAsync<ProjectActivityPage>(
                    $"/api/v1/projects/{project.Id}/activity?limit=4", timeout.Token);
                Assert.NotNull(first);
                Assert.Equal(4, first.Count);
                Assert.NotNull(first.NextCursor);

                var second = await client.GetFromJsonAsync<ProjectActivityPage>(
                    $"/api/v1/projects/{project.Id}/activity?limit=4&cursor={Uri.EscapeDataString(first.NextCursor!)}",
                    timeout.Token);
                Assert.NotNull(second);
                Assert.Equal(2, second.Count);
                Assert.Null(second.NextCursor);

                var paged = first.Items.Concat(second.Items)
                    .Select(item => (item.EntityKind, item.EntityId)).ToArray();
                Assert.Equal(6, paged.Length);
                Assert.Equal(6, paged.Distinct().Count());
                Assert.Equal(
                    full.Items.Select(item => (item.EntityKind, item.EntityId)).ToArray(),
                    paged);

                // Escopo por tenant: o endpoint resolve o projeto DENTRO do tenant da sessão. Um id
                // bem-formado de outro tenant (inexistente para este) devolve 404, nunca dados alheios.
                var foreignProjectId = UlidValue.New(DateTimeOffset.UtcNow).ToString();
                using var foreign = await client.GetAsync(
                    $"/api/v1/projects/{foreignProjectId}/activity", timeout.Token);
                Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

                // Cursor malformado é recusado de forma tipada.
                using var badCursor = await client.GetAsync(
                    $"/api/v1/projects/{project.Id}/activity?cursor=!!!notbase64", timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, badCursor.StatusCode);
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task SeedChainAsync(
        IServiceProvider services, string profileId, string projectId, string demandTitle,
        DateTimeOffset occurredAt, CancellationToken token)
    {
        var profile = await services.GetRequiredService<ILocalProfileStore>().GetAsync(profileId, token)
            ?? throw new InvalidOperationException("Profile was not persisted.");
        var solicitationId = UlidValue.New(occurredAt).ToString();
        var demandId = UlidValue.New(occurredAt.AddTicks(1)).ToString();
        var taskId = UlidValue.New(occurredAt.AddTicks(2)).ToString();
        var instructionId = UlidValue.New(occurredAt.AddTicks(3)).ToString();
        const string instruction = "Do the work.";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instruction)));
        var store = new SqliteWorkChainStore(services.GetRequiredService<SqliteWriteDispatcher>());
        await store.CreateAsync(
            new WorkChainCreateCommand(
                profile.TenantId, projectId, profileId, solicitationId, "A request.", demandId,
                demandTitle, "[\"Done\"]", taskId, "A task", "medium", 1m, instructionId,
                instruction, hash, $"seed-{taskId}", occurredAt),
            token);
    }

    private static async Task<ProfileResponse> CreateProfileAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR"), token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileResponse>(token))!;
    }

    private static async Task<OrganizationResponse> CreateOrganizationAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/organizations", new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationResponse>(token))!;
    }

    private static async Task<ProjectResponse> CreateProjectAsync(HttpClient client, string organizationId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest
        {
            OrganizationId = organizationId,
            Name = "Poseidon",
            Key = "POSEIDON",
            Description = "Backend",
        }, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>(token))!;
    }

    private static WebApplication CreateHost(string database) => HostApplication.Build(
        ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(x => x.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
