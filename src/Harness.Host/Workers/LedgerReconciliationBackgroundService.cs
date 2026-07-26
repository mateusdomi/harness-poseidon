using Harness.Modules.Governance.Ledger;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harness.Host.Workers;

public sealed record LedgerReconciliationOptions
{
    /// <summary>Intervalo entre ciclos. O primeiro ciclo roda um período APÓS o boot.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Tamanho da página de leitura da cadeia crua.</summary>
    public int PageSize { get; init; } = 2000;
}

/// <summary>
/// Fase 12 — reconciliação PERIÓDICA do `audit_ledger`: em cada ciclo, lê a cadeia crua de cada
/// tenant, reverifica sequência e hashes com o <see cref="ILedgerReconciliationService"/> e
/// grava o resultado como evento `ledger.reconciliationCompleted` no próprio ledger — a
/// verificação de integridade vira fato auditável e recorrente, não uma checagem manual.
/// Uma cadeia inválida NUNCA é corrigida automaticamente: o evento carrega as sequências
/// divergentes e o operador decide (Default-FAIL: detectar e escalar, jamais mascarar).
/// </summary>
public sealed partial class LedgerReconciliationBackgroundService(
    IServiceScopeFactory scopes,
    LedgerReconciliationOptions options,
    ILedgerReconciliationService reconciliation,
    IClock clock,
    ILogger<LedgerReconciliationBackgroundService> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Ledger do tenant {TenantId} INVÁLIDO: {Tampered} sequência(s) divergente(s).")]
    private static partial void LogChainInvalid(ILogger logger, string tenantId, long tampered);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ciclo de reconciliação do ledger falhou: {ErrorType}")]
    private static partial void LogCycleFailed(ILogger logger, string errorType);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogCycleFailed(logger, exception.GetType().Name);
            }
        }
    }

    /// <summary>Um ciclo completo, também invocável sob demanda (endpoint e testes).</summary>
    public async Task<IReadOnlyList<LedgerReconciliationResult>> RunCycleAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var profiles = scope.ServiceProvider.GetRequiredService<ILocalProfileStore>();
        var ledger = scope.ServiceProvider.GetRequiredService<IAuditEventStore>();

        var results = new List<LedgerReconciliationResult>();
        foreach (var profile in await profiles.ListAsync(cancellationToken))
        {
            var result = await ReconcileTenantAsync(ledger, profile.TenantId, cancellationToken);
            results.Add(result);
            if (!result.IsChainValid)
            {
                LogChainInvalid(logger, profile.TenantId, result.TamperedCount);
            }

            // O resultado vira evento do PRÓPRIO ledger: reconciliações são parte da história
            // auditável. O detalhe carrega só números e hashes — nunca payload de evento.
            _ = await ledger.AppendAsync(
                new AuditEventAppendCommand(
                    profile.TenantId,
                    "system",
                    "ledger-reconciliation",
                    "ledger.reconciliationCompleted",
                    "system",
                    null,
                    $"valid={result.IsChainValid}; entries={result.TotalEntries}; " +
                    $"tampered={result.TamperedCount}; lastValidHash={result.LastValidHash}",
                    clock.UtcNow),
                cancellationToken);
        }

        return results;
    }

    /// <summary>Lê a cadeia crua em páginas e reconcilia o tenant inteiro.</summary>
    public async Task<LedgerReconciliationResult> ReconcileTenantAsync(
        IAuditEventStore ledger,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var entries = new List<AuditLedgerEntry>();
        var after = 0L;
        while (true)
        {
            var page = await ledger.ListChainAsync(
                tenantId, after, Math.Max(1, options.PageSize), cancellationToken);
            entries.AddRange(page.Select(row => new AuditLedgerEntry(
                row.Sequence, tenantId, row.EventType, row.PayloadJson,
                row.PreviousHash, row.EventHash, row.OccurredAt)));
            if (page.Count < Math.Max(1, options.PageSize))
            {
                break;
            }

            after = page[^1].Sequence;
        }

        return reconciliation.Reconcile(tenantId, entries);
    }
}
