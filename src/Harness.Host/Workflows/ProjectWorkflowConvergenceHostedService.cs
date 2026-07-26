using System.Diagnostics.CodeAnalysis;
using Harness.Host.Agents;
using Harness.Persistence.Abstractions.Identity;

namespace Harness.Host.Workflows;

/// <summary>
/// RN-02: no startup, vincula o workflow recomendado a TODO projeto sem workflow — e religa ao
/// recomendado projetos presos a um template canônico LEGADO (pré-playbook) — para que nunca
/// exista projeto fora da esteira canônica. Roda SEMPRE: a invariante do workflow não depende
/// de execução de agentes habilitada nem de conta do Chefe (o launcher desktop não liga
/// AgentRuns e ainda assim o chat precisa da fase certa).
///
/// Nunca impede o boot: uma falha idempotente é logada e reexecuta no próximo restart — o mesmo
/// padrão dos demais seeders de inicialização. O próprio seed é idempotente; este serviço apenas
/// decide se roda e para quais tenants.
/// </summary>
public sealed class ProjectWorkflowConvergenceHostedService(
    ProjectWorkflowConvergenceSeeder seeder,
    ILocalProfileStore profiles,
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


    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Idempotent startup seeding must never prevent the Host from booting; it retries on restart.")]
    public async Task StartAsync(CancellationToken cancellationToken)
    {
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
