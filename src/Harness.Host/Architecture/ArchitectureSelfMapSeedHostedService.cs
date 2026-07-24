using System.Diagnostics.CodeAnalysis;
using Harness.Host.Agents;
using Harness.Persistence.Abstractions.Identity;

namespace Harness.Host.Architecture;

/// <summary>
/// UX-PROTO/ARCH: no startup, quando a execução de agentes está ligada, semeia (de forma idempotente)
/// o MAPA DE ARQUITETURA do próprio Poseidon no Architecture Hub para que a tela <c>/architecture</c>
/// deixe de nascer vazia — o dono passa a abrir o projeto e ver o produto já mapeado. Gateado APENAS
/// por <c>AgentRuns.Enabled</c>, como <c>ProjectStateConvergenceHostedService</c>. Roda por tenant.
///
/// Nunca impede o boot: uma falha idempotente é logada e reexecuta no próximo restart — o mesmo
/// padrão dos demais seeders. O próprio seed é idempotente; este serviço só decide se roda e para
/// quais tenants.
/// </summary>
public sealed class ArchitectureSelfMapSeedHostedService(
    ArchitectureSelfMapSeeder seeder,
    ILocalProfileStore profiles,
    AgentRunSettings settings,
    ILogger<ArchitectureSelfMapSeedHostedService> logger) : IHostedService
{
    private static readonly Action<ILogger, string, int, int, Exception?> Seeded =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Information,
            new EventId(1015, nameof(Seeded)),
            "UX-PROTO/ARCH: mapeou a arquitetura do Poseidon no tenant {TenantId} " +
            "({Elements} elementos, {Relationships} relacionamentos).");
    private static readonly Action<ILogger, string, Exception?> Skipped =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1016, nameof(Skipped)),
            "UX-PROTO/ARCH: mapeamento da arquitetura do Poseidon ignorado: {Reason}.");
    private static readonly Action<ILogger, string, Exception?> Failed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1017, nameof(Failed)),
            "UX-PROTO/ARCH: mapeamento da arquitetura do Poseidon ignorado no startup: {Reason}. " +
            "Reexecuta no próximo restart.");

    /// <summary>Gate PURO e testável: semeia somente quando a execução de agentes está habilitada.</summary>
    public static bool ShouldRun(AgentRunSettings? settings) => settings?.Enabled == true;

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Idempotent startup seeding must never prevent the Host from booting; it retries on restart.")]
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!ShouldRun(settings))
        {
            Skipped(logger, "agent runs are disabled", null);
            return;
        }

        try
        {
            var tenants = (await profiles.ListAsync(cancellationToken))
                .Select(profile => profile.TenantId)
                .Distinct(StringComparer.Ordinal);
            foreach (var tenantId in tenants)
            {
                var result = await seeder.EnsureSeededAsync(tenantId, cancellationToken);
                if (result.ChangedAnything)
                {
                    Seeded(logger, tenantId, result.ElementsCreated, result.RelationshipsCreated, null);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Failed(logger, exception.GetType().Name, exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
