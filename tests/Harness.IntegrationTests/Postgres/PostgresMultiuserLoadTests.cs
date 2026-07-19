using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Harness.IntegrationTests.Postgres;

public sealed class PostgresMultiuserLoadTests
{
    private const int UserCount = 30;

    [Fact]
    public async Task ThirtyConcurrentUsersShareOneTenantWithIsolationAndRateLimit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"multiuser-load-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sessions = new List<UserSession>();
        await using var fixture = await PostgresSkipLockedPocTests.ManagedPostgresFixture
            .StartAsync(timeout.Token);

        try
        {
            await using var app = HostApplication.Build(
            [
                "--urls",
                "http://127.0.0.1:0",
                "--Harness:DatabasePath",
                Path.Combine(root, "unused.db"),
                "--Harness:Database:Provider",
                "postgres",
                "--Harness:Database:ConnectionString",
                fixture.ConnectionString,
                "--Harness:Server:RateLimitPermitsPerMinute",
                "2000",
            ]);
            await app.StartAsync(timeout.Token);
            try
            {
                var address = Address(app.Services);
                var users = await SpawnUsersConcurrentlyAsync(address, sessions, timeout.Token);
                Assert.Equal(UserCount, users.Count);
                Assert.Equal(UserCount, users.Select(user => user.Profile.Id).Distinct().Count());

                await AssertSingleTenantAsync(fixture.ConnectionString, timeout.Token);
                await CreateWorkspacesConcurrentlyAsync(users, timeout.Token);
                await AssertAdversarialIsolationAsync(address, users, timeout.Token);
                await AssertRateLimiterKicksInAsync(address, timeout.Token);
            }
            finally
            {
                foreach (var session in sessions)
                {
                    session.Dispose();
                }

                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class UserSession(Uri address) : IDisposable
    {
        private readonly HttpClientHandler _handler = new() { CookieContainer = new CookieContainer() };

        public HttpClient Client => _client ??= new HttpClient(_handler) { BaseAddress = address };

        private HttpClient? _client;

        public ProfileResponse Profile { get; set; } = null!;

        public void Dispose()
        {
            _client?.Dispose();
            _handler.Dispose();
        }
    }

    private static async Task<IReadOnlyList<UserSession>> SpawnUsersConcurrentlyAsync(
        Uri address,
        List<UserSession> sessions,
        CancellationToken token)
    {
        // Todos os 30 usuários disparam a criação ao mesmo tempo: o primeiro vence o
        // bootstrap do tenant e os demais aderem via retry de corrida no endpoint.
        sessions.AddRange(Enumerable.Range(0, UserCount).Select(_ => new UserSession(address)));
        var results = await Task.WhenAll(sessions.Select(async (session, index) =>
        {
            using var response = await session.Client.PostAsJsonAsync(
                "/api/v1/profiles",
                new CreateProfileRequest($"Usuário {index:D2}", $"user{index:D2}@poseidon.local", null, "pt-BR"),
                token);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            session.Profile = (await response.Content.ReadFromJsonAsync<ProfileResponse>(token))!;
            return session;
        }));
        return results;
    }

    private static async Task AssertSingleTenantAsync(string connectionString, CancellationToken token)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using (var tenants = dataSource.CreateCommand("SELECT COUNT(*) FROM harness.tenants;"))
        {
            Assert.Equal(1L, await tenants.ExecuteScalarAsync(token));
        }

        await using var profiles = dataSource.CreateCommand("SELECT COUNT(*) FROM harness.local_users;");
        Assert.Equal((long)UserCount, await profiles.ExecuteScalarAsync(token));
    }

    private static async Task CreateWorkspacesConcurrentlyAsync(
        IReadOnlyList<UserSession> users,
        CancellationToken token)
    {
        await Task.WhenAll(users.Select(async (user, index) =>
        {
            using var organizationResponse = await user.Client.PostAsJsonAsync(
                "/api/v1/organizations",
                new CreateOrganizationRequest { Name = $"Carga {index:D2}", Slug = $"carga-{index:D2}" },
                token);
            Assert.Equal(HttpStatusCode.Created, organizationResponse.StatusCode);
            var organization = (await organizationResponse.Content
                .ReadFromJsonAsync<OrganizationResponse>(token))!;
            using var projectResponse = await user.Client.PostAsJsonAsync(
                "/api/v1/projects",
                new CreateProjectRequest
                {
                    OrganizationId = organization.Id,
                    Name = $"Projeto {index:D2}",
                    Key = $"LOAD{index:D2}",
                    Description = "Teste de carga multiusuário",
                },
                token);
            Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        }));
    }

    private static async Task AssertAdversarialIsolationAsync(
        Uri address,
        IReadOnlyList<UserSession> users,
        CancellationToken token)
    {
        // Sessão B não pode mutar o perfil de A, mesmo autenticada.
        using (var hijack = await users[1].Client.PatchAsJsonAsync(
            $"/api/v1/profiles/{users[0].Profile.Id}",
            new UpdateProfileRequest { DisplayName = "Invasor" },
            token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, hijack.StatusCode);
        }

        // Cookie forjado com ULID válido mas inexistente não abre sessão.
        using var forgedHandler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        forgedHandler.CookieContainer.Add(
            address,
            new Cookie("harness.profile", "01JZZZZZZZZZZZZZZZZZZZZZZZ"));
        using var forged = new HttpClient(forgedHandler) { BaseAddress = address };
        using (var current = await forged.GetAsync(
            new Uri("/api/v1/profiles/current", UriKind.Relative), token))
        {
            Assert.Equal(HttpStatusCode.NotFound, current.StatusCode);
        }

        // Sem cookie algum, não existe sessão.
        using var anonymous = new HttpClient { BaseAddress = address };
        using var anonymousCurrent = await anonymous.GetAsync(
            new Uri("/api/v1/profiles/current", UriKind.Relative), token);
        Assert.Equal(HttpStatusCode.NotFound, anonymousCurrent.StatusCode);
    }

    private static async Task AssertRateLimiterKicksInAsync(Uri address, CancellationToken token)
    {
        using var client = new HttpClient { BaseAddress = address };
        var rateLimited = false;
        for (var attempt = 0; attempt < 2100 && !rateLimited; attempt++)
        {
            using var response = await client.GetAsync(
                new Uri("/health", UriKind.Relative), token);
            rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests;
        }

        Assert.True(rateLimited, "O rate limiter do modo servidor não respondeu 429.");
    }

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value =>
            value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
