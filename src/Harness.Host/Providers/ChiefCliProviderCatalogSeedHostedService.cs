using System.Diagnostics.CodeAnalysis;
using Harness.Host.Agents;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;
using Harness.Persistence.Abstractions.Identity;

namespace Harness.Host.Providers;

/// <summary>
/// GP-06 (fecho): na inicialização, quando o recurso de execução de agentes está ligado E a conta
/// do Chefe está presente/habilitada no registro, converge o catálogo de providers de cada tenant
/// para o estado EXECUTÁVEL (conta+modelo reais ativos, chefe roteado, workflow vinculado). O
/// próprio seed é idempotente; este serviço apenas decide se roda e para quais tenants.
///
/// Nunca impede o boot: uma falha idempotente é logada e reexecuta no próximo restart — exatamente
/// o padrão dos demais seeders de inicialização.
/// </summary>
public sealed class ChiefCliProviderCatalogSeedHostedService(
    ChiefCliProviderCatalogSeeder seeder,
    ILocalProfileStore profiles,
    AgentAccountRegistry registry,
    AgentRunSettings settings,
    ILogger<ChiefCliProviderCatalogSeedHostedService> logger) : IHostedService
{
    private static readonly Action<ILogger, int, string, Exception?> Seeded =
        LoggerMessage.Define<int, string>(
            LogLevel.Information,
            new EventId(1006, nameof(Seeded)),
            "Converged the Chief CLI provider catalog for tenant {TenantId} ({Changes} change(s)).");
    private static readonly Action<ILogger, string, Exception?> Skipped =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1007, nameof(Skipped)),
            "Chief CLI provider catalog seeding was skipped: {Reason}.");
    private static readonly Action<ILogger, string, Exception?> Failed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1008, nameof(Failed)),
            "Chief CLI provider catalog seeding was skipped at startup: {Reason}. " +
            "Seeding retries on the next restart.");

    /// <summary>
    /// Gate PURO e testável: só semeia quando o recurso está habilitado E existe no registro uma
    /// conta do papel <c>chief-orchestrator</c> que não está desabilitada. É exatamente o critério
    /// que o <c>ConversationChiefAgentExecutor</c> usa para resolver a conta do Chefe: se ele
    /// aceitaria a conta, o gate reflete isso como executável. A autenticação em si é do keychain,
    /// comprovada pela CLI no turno — presumir aqui seria desonesto, barrar por ela seria frágil.
    /// </summary>
    public static bool ShouldSeed(AgentRunSettings? settings, AgentAccountRegistry? registry)
    {
        if (settings is null || !settings.Enabled || registry is null)
        {
            return false;
        }

        return registry.List().Any(account =>
            account.State != AgentAccountState.Disabled &&
            account.AllowedRoles.Contains(AgentRoles.ChiefOrchestrator, StringComparer.OrdinalIgnoreCase));
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Idempotent startup seeding must never prevent the Host from booting; it retries on restart.")]
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!ShouldSeed(settings, registry))
        {
            Skipped(logger, settings.Enabled ? "no chief-orchestrator account is present" : "agent runs are disabled", null);
            return;
        }

        try
        {
            var tenants = (await profiles.ListAsync(cancellationToken))
                .GroupBy(profile => profile.TenantId, StringComparer.Ordinal)
                .Select(group => (TenantId: group.Key, ActorProfileId: group.First().Id));
            foreach (var (tenantId, actorProfileId) in tenants)
            {
                var result = await seeder.EnsureSeededAsync(tenantId, actorProfileId, cancellationToken);
                if (result.ChangedAnything)
                {
                    Seeded(logger, ChangeCount(result), tenantId, null);
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

    private static int ChangeCount(ChiefCliCatalogSeedResult result) =>
        (result.AccountCreated ? 1 : 0) + (result.AccountActivated ? 1 : 0) +
        (result.ModelCreated ? 1 : 0) + (result.ModelEnabled ? 1 : 0) +
        result.AgentsWired + result.WorkflowsBound;
}
