using System.Diagnostics.CodeAnalysis;
using Harness.Host.Agents;
using Harness.Persistence.Abstractions.Identity;

namespace Harness.Host.Projects;

/// <summary>
/// RN-03: no startup, quando a execução de agentes está ligada, converge o ESTADO REAL do projeto
/// Poseidon (fase da run, protótipo do front, marca da organização) para que o cockpit/chat nunca o
/// mostrem "sem fase / 0% / sem protótipo / sem marca". Gateado APENAS por <c>AgentRuns.Enabled</c>,
/// como o <c>ProjectWorkflowConvergenceHostedService</c> — não depende de conta do Chefe. Roda depois
/// da convergência de workflow (registrada antes), então o binding já existe quando esta roda.
///
/// Nunca impede o boot: uma falha idempotente é logada e reexecuta no próximo restart — o mesmo
/// padrão dos demais seeders. O próprio seed é idempotente; este serviço só decide se roda e para
/// quais tenants.
/// </summary>
public sealed class ProjectStateConvergenceHostedService(
    ProjectStateConvergenceSeeder seeder,
    ILocalProfileStore profiles,
    AgentRunSettings settings,
    ILogger<ProjectStateConvergenceHostedService> logger) : IHostedService
{
    private static readonly Action<ILogger, string, string, Exception?> Converged =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1012, nameof(Converged)),
            "RN-03: convergiu o estado do projeto Poseidon no tenant {TenantId} ({Changes}).");
    private static readonly Action<ILogger, string, Exception?> Skipped =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1013, nameof(Skipped)),
            "RN-03: convergência de estado do projeto ignorada: {Reason}.");
    private static readonly Action<ILogger, string, Exception?> Failed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1014, nameof(Failed)),
            "RN-03: convergência de estado do projeto ignorada no startup: {Reason}. " +
            "Reexecuta no próximo restart.");

    /// <summary>Gate PURO e testável: converge somente quando a execução de agentes está habilitada.</summary>
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
                .GroupBy(profile => profile.TenantId, StringComparer.Ordinal)
                .Select(group => (TenantId: group.Key, ActorProfileId: group.First().Id));
            foreach (var (tenantId, actorProfileId) in tenants)
            {
                var result = await seeder.EnsureConvergedAsync(tenantId, actorProfileId, cancellationToken);
                if (result.ChangedAnything)
                {
                    Converged(logger, tenantId, Describe(result), null);
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

    private static string Describe(ProjectStateConvergenceResult result)
    {
        var parts = new List<string>(3);
        if (result.RunStarted)
        {
            parts.Add("run iniciada");
        }

        if (result.PrototypeRegistered)
        {
            parts.Add("protótipo do front");
        }

        if (result.BrandFilled)
        {
            parts.Add("marca da organização");
        }

        return string.Join(", ", parts);
    }
}
