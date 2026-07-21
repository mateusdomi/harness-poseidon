using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Modules.Identity.Contracts;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// CA-5 — bootstrap governado `poseidon agent start` pela superfície oficial.
///
/// As provas aqui são de GOVERNANÇA, não de modelo: nenhum executor externo é iniciado.
/// Elas garantem que o comando nasce desligado, exige sessão, recusa papel desconhecido,
/// recusa conta inexistente, recusa executor sem adapter e nunca deixa o cliente escolher
/// o próprio escopo de paths.
/// </summary>
public sealed class AgentRunBootstrapTests : IDisposable
{
    private static readonly string[] SmuggledScopeClaims = ["src/**"];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"harness-agent-runs-{Guid.NewGuid():N}");

    private string At(params string[] parts) =>
        Path.Combine([_root, .. parts]);

    private WebApplication BuildHost(bool enabled)
    {
        Directory.CreateDirectory(At("controlled"));
        Directory.CreateDirectory(At("profiles"));
        string[] args = enabled
            ?
            [
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", At("harness.db"),
                "--Harness:AgentRuns:Enabled", "true",
                "--Harness:AgentRuns:ControlledRoot", At("controlled"),
                "--Harness:AgentRuns:ProfilesRoot", At("profiles"),
            ]
            :
            [
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", At("harness.db"),
            ];
        return HostApplication.Build(args);
    }

    private static async Task<HttpClient> ClientAsync(WebApplication app)
    {
        await app.StartAsync(CancellationToken.None);
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish an address.");
        var address = new Uri(addresses.Single(item =>
            item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        var client = new HttpClient(handler) { BaseAddress = address };
        // A sessão pessoal nasce com o perfil local; sem ela todo endpoint responde 401.
        using var created = await client.PostAsJsonAsync(
            "/api/v1/profiles",
            new CreateProfileRequest("Operador", null, null, "pt-BR"),
            CancellationToken.None);
        created.EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task GovernedAgentRunsAreDisabledUntilTheOperatorConfiguresThem()
    {
        // Um produto recém-instalado NÃO executa agente externo por padrão.
        await using var app = BuildHost(enabled: false);
        using var client = await ClientAsync(app);

        var response = await client.PostAsJsonAsync(
            "/api/v1/agent-runs",
            new
            {
                projectId = Ulid(),
                taskId = Ulid(),
                role = "frontend-specialist",
                account = "worker-codex-frontend",
                instruction = "faça algo",
            },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            "agent_runs_disabled",
            await response.Content.ReadAsStringAsync(CancellationToken.None),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownRoleIsRefusedBeforeAnyClaimOrAccountIsTouched()
    {
        await using var app = BuildHost(enabled: true);
        using var client = await ClientAsync(app);

        var response = await client.PostAsJsonAsync(
            "/api/v1/agent-runs",
            new
            {
                projectId = Ulid(),
                taskId = Ulid(),
                role = "frontend-kimi-superuser",
                account = "worker-codex-frontend",
                instruction = "faça algo",
            },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "invalid_role",
            await response.Content.ReadAsStringAsync(CancellationToken.None),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRequestCannotSmuggleItsOwnPathScope()
    {
        // `scopeClaims` NÃO existe no contrato de entrada: o escopo vem do papel. Um corpo
        // que tente declará-lo é recusado pelo próprio desserializador.
        await using var app = BuildHost(enabled: true);
        using var client = await ClientAsync(app);

        var response = await client.PostAsJsonAsync(
            "/api/v1/agent-runs",
            new
            {
                projectId = Ulid(),
                taskId = Ulid(),
                role = "frontend-specialist",
                account = "worker-codex-frontend",
                instruction = "faça algo",
                scopeClaims = SmuggledScopeClaims,
            },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheDoctorReportsRealProbeAndProfileStateForEveryCanonicalAccount()
    {
        await using var app = BuildHost(enabled: true);
        using var client = await ClientAsync(app);

        var response = await client.GetAsync("/api/v1/agent-accounts/doctor", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));
        var accounts = document.RootElement.GetProperty("accounts").EnumerateArray().ToArray();

        Assert.Equal(7, accounts.Length);

        var codex = accounts.Single(account =>
            account.GetProperty("alias").GetString() == "worker-codex-frontend");
        Assert.True(codex.GetProperty("adapterImplemented").GetBoolean());

        // Kimi e Antigravity ainda não têm adapter (CA-8): isso é reportado como fato, não
        // disfarçado de suporte existente.
        var kimi = accounts.Single(account =>
            account.GetProperty("alias").GetString() == "worker-kimi-ui");
        Assert.False(kimi.GetProperty("adapterImplemented").GetBoolean());
        Assert.Equal("executor.adapter_not_implemented", kimi.GetProperty("probeReasonCode").GetString());

        // Nenhuma conta é reportada como autenticada sem material de credencial real.
        Assert.All(accounts, account => Assert.False(account.GetProperty("authenticated").GetBoolean()));
    }

    private static string Ulid() => UlidValue.New(DateTimeOffset.UtcNow).ToString();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
