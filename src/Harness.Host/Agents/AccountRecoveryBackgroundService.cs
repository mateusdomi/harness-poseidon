using Harness.Modules.Agents.Application.Accounts;
using Harness.SharedKernel.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harness.Host.Agents;

/// <summary>
/// Agendador de RETOMADA de contas (peça do loop autônomo de cota/retry). A cada ciclo
/// reabilita, no ledger durável, as contas cujo cooldown de cota/transitório já venceu — de
/// modo que a conta volte a receber trabalho quando a janela reseta, sem ação humana. Contas
/// em <c>AuthenticationRequired</c> NÃO voltam sozinhas (exigem login).
///
/// Só reabilita a DISPONIBILIDADE; o re-despacho do trabalho pendente é decisão do nível
/// superior (Chief). O serviço é resiliente: uma falha de ciclo é registrada e não derruba o
/// laço.
///
/// E reabilitar no ledger não basta: quem ELEGE a conta lê o REGISTRO, que era hidratado uma
/// única vez, na subida do Host. Uma conta que se recuperasse depois disso continuava fora da
/// eleição até o próximo reinício — enquanto o painel, que lê o ledger, a mostrava disponível.
/// Foi o que tirou de circulação, por horas, a única conta sã de especialista em 2026-08-03.
/// Por isso o ciclo aplica o ledger ao registro TODA vez, e não só quando houve recuperação.
/// </summary>
public sealed partial class AccountRecoveryBackgroundService(
    AccountAvailabilityLedger availability,
    AgentAccountRegistry registry,
    IClock clock,
    ILogger<AccountRecoveryBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "{Count} conta(s) reabilitada(s) após reset de cota/cooldown.")]
    private static partial void LogRecovered(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Ciclo de retomada de contas falhou: {ErrorType}")]
    private static partial void LogFailure(ILogger logger, string errorType);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var recovered = availability.RecoverExpired(clock.UtcNow);
                if (recovered.Count > 0)
                {
                    LogRecovered(logger, recovered.Count);
                }

                // Sempre, e não só quando houve recuperação: o ledger também muda por observação
                // de terceiros (doctor, execução real), e o registro precisa refletir o que o
                // sistema já sabe. É idempotente e não toca conta reservada.
                _ = registry.ApplyObservedAvailability(availability.List());
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Só o TIPO do erro — nunca conteúdo sensível. Um ciclo com falha não derruba o laço.
                LogFailure(logger, exception.GetType().Name);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
