using System.Diagnostics.CodeAnalysis;
using Harness.Host.Agents;
using Harness.Persistence.Abstractions.Identity;

namespace Harness.Host.Workflows;

/// <summary>
/// RN-02: no startup, quando a execução de agentes está ligada, vincula o workflow recomendado a
/// TODO projeto sem workflow, para que nunca exista um projeto sem fase/workflow — condição para
/// conversar com o Chefe. Diferente do <c>ChiefCliProviderCatalogSeedHostedService</c> (GP-06, que
/// exige uma conta do Chefe presente), esta convergência é gateada APENAS por <c>AgentRuns.Enabled</c>:
/// a invariante do workflow não depende de haver uma conta de execução do Chefe configurada.
///
/// Nunca impede o boot: uma falha idempotente é logada e reexecuta no próximo restart — o mesmo
/// padrão dos demais seeders de inicialização. O próprio seed é idempotente; este serviço apenas
/// decide se roda e para quais tenants.
/// </summary>
public sealed class ProjectWorkflowConvergenceHostedService(
    ProjectWorkflowConvergenceSeeder seeder,
    ILocalProfileStore profiles,
    AgentRunSettings settings,
    ILogger<ProjectWorkflowConvergenceHostedService> logger) : IHostedService
{
    private static readonly Action<ILogger, int, string, Exception?> Bound =
        LoggerMessage.Define<int, string>(
            LogLevel.Information,
            new EventId(1009, nameof(Bound)),
            "RN-02: vinculou o workflow recomendado a {Count} projeto(s) sem workflow no tenant {TenantId}.");
    private static readonly Action<ILogger, string, Exception?> Skipped =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1010, nameof(Skipped)),
            "RN-02: convergência de workflow ignorada: {Reason}.");
    private static readonly Action<ILogger, string, Exception?> Failed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1011, nameof(Failed)),
            "RN-02: convergência de workflow ignorada no startup: {Reason}. " +
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
                var bound = await seeder.EnsureBoundAsync(tenantId, actorProfileId, cancellationToken);
                if (bound > 0)
                {
                    Bound(logger, bound, tenantId, null);
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
