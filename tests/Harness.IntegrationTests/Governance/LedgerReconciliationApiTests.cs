using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Host.Workers;
using Harness.Modules.Identity.Contracts;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Governance;

/// <summary>
/// Fase 12 — reconciliação do ledger no caminho REAL: a cadeia crua persistida é relida e
/// reverificada hash a hash pelo serviço de reconciliação — sob demanda (endpoint) e em ciclo
/// (job) — e cada reconciliação vira evento do próprio ledger. Nada é corrigido em silêncio:
/// o veredito carrega contagens e sequências divergentes para decisão do operador.
/// </summary>
public sealed class LedgerReconciliationApiTests
{
    [Fact]
    public async Task ReconciliationVerifiesTheRealChainAndRecordsItselfInTheLedger()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"ledger-reconciliation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build([
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", Path.Combine(root, "ledger.db"),
            ]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };

                using var profile = await client.PostAsJsonAsync(
                    "/api/v1/profiles",
                    new CreateProfileRequest("Operador", null, null, "pt-BR"),
                    timeout.Token);
                profile.EnsureSuccessStatusCode();

                // Popular o ledger com eventos reais (perfil criado já gera; adiciona mais).
                var tenantId = (await app.Services.GetRequiredService<ILocalProfileStore>()
                    .ListAsync(timeout.Token))[0].TenantId;
                var ledger = app.Services.GetRequiredService<IAuditEventStore>();
                for (var index = 0; index < 3; index++)
                {
                    _ = await ledger.AppendAsync(
                        new AuditEventAppendCommand(
                            tenantId, "system", "seed", $"test.seedEvent{index}", "system", null,
                            $"evento {index}", DateTimeOffset.UtcNow),
                        timeout.Token);
                }

                // Sob demanda: a cadeia real é válida e o veredito é numérico e completo.
                using var response = await client.GetAsync(
                    "/api/v1/governance-runtime/ledger-reconciliation", timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(timeout.Token));
                Assert.True(body.RootElement.GetProperty("isChainValid").GetBoolean());
                Assert.True(body.RootElement.GetProperty("totalEntries").GetInt64() >= 4);
                Assert.Equal(0, body.RootElement.GetProperty("tamperedCount").GetInt64());
                Assert.Matches(
                    "^[0-9A-Fa-f]{64}$",
                    body.RootElement.GetProperty("lastValidHash").GetString()!);
                Assert.Empty(
                    body.RootElement.GetProperty("discrepancySequenceNumbers").EnumerateArray());

                // A reconciliação em si virou fato do ledger.
                var events = await ledger.ListAsync(
                    new AuditEventQuery(tenantId, null, 100), timeout.Token);
                var recorded = Assert.Single(
                    events, entry => entry.Action == "ledger.reconciliationCompleted");
                Assert.Contains("valid=True", recorded.Detail!, StringComparison.Ordinal);

                // O ciclo do job (mesmo código do BackgroundService) reconcilia e registra de
                // novo — inclusive validando o evento que a reconciliação anterior acrescentou.
                var job = app.Services.GetRequiredService<LedgerReconciliationBackgroundService>();
                var results = await job.RunCycleAsync(timeout.Token);
                var cycle = Assert.Single(results);
                Assert.True(cycle.IsChainValid);
                Assert.True(cycle.TotalEntries > body.RootElement.GetProperty("totalEntries").GetInt64());

                // E a cadeia continua íntegra depois de tudo (o verificador do store concorda).
                Assert.True((await ledger.VerifyIntegrityAsync(tenantId, timeout.Token)).Valid);
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

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish an address.");
        return new Uri(addresses.Single(item =>
            item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
