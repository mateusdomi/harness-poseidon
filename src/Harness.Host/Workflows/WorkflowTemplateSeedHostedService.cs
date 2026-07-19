using System.Diagnostics.CodeAnalysis;
using Harness.Persistence.Abstractions.Identity;

namespace Harness.Host.Workflows;

public sealed class WorkflowTemplateSeedHostedService(
    WorkflowTemplateSeeder seeder,
    ILocalProfileStore profiles,
    ILogger<WorkflowTemplateSeedHostedService> logger) : IHostedService
{
    private static readonly Action<ILogger, int, string, Exception?> TemplatesSeeded =
        LoggerMessage.Define<int, string>(
            LogLevel.Information,
            new EventId(1002, nameof(TemplatesSeeded)),
            "Seeded {TemplateCount} canonical workflow template(s) for tenant {TenantId}.");
    private static readonly Action<ILogger, string, Exception?> SeedSkipped =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1003, nameof(SeedSkipped)),
            "Canonical workflow template seeding was skipped at startup: {Reason}. " +
            "Seeding retries on the next profile creation or restart.");

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Idempotent startup seeding must never prevent the Host from booting; it retries on profile creation.")]
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tenants = (await profiles.ListAsync(cancellationToken))
                .Select(profile => profile.TenantId)
                .Distinct(StringComparer.Ordinal);
            foreach (var tenantId in tenants)
            {
                var seeded = await seeder.EnsureSeededAsync(tenantId, cancellationToken);
                if (seeded > 0)
                {
                    TemplatesSeeded(logger, seeded, tenantId, null);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            SeedSkipped(logger, exception.GetType().Name, exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
