using System.Diagnostics.CodeAnalysis;

namespace Harness.Host.Agents;

/// <summary>
/// CAT-02: preenche o conteúdo canônico das definições built-in na inicialização do Host,
/// espelhando o padrão do <c>WorkflowTemplateSeedHostedService</c>. O seed é global
/// (tenant_id IS NULL) e idempotente: só semeia linhas ainda vazias.
/// </summary>
public sealed class BuiltInAgentDefinitionSeedHostedService(
    BuiltInAgentDefinitionSeeder seeder,
    ILogger<BuiltInAgentDefinitionSeedHostedService> logger) : IHostedService
{
    private static readonly Action<ILogger, int, Exception?> DefinitionsSeeded =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1004, nameof(DefinitionsSeeded)),
            "Seeded canonical persona content for {DefinitionCount} built-in agent definition(s).");
    private static readonly Action<ILogger, string, Exception?> SeedSkipped =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1005, nameof(SeedSkipped)),
            "Canonical built-in agent-definition seeding was skipped at startup: {Reason}. " +
            "Seeding retries on the next restart.");

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Idempotent startup seeding must never prevent the Host from booting; it retries on restart.")]
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var seeded = await seeder.EnsureSeededAsync(cancellationToken);
            if (seeded > 0)
            {
                DefinitionsSeeded(logger, seeded, null);
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
