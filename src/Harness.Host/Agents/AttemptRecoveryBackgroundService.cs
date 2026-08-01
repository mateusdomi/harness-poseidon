using Harness.Persistence.Abstractions.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harness.Host.Agents;

/// <summary>
/// Recuperação automática de tentativas ÓRFÃS.
///
/// Uma tentativa em execução segura duas coisas: a concessão da conta e a CLAIM DE PATH do card.
/// Se o processo do Host morre (crash, restart, deploy) no meio de uma execução, nada devolve
/// essas duas ao pool — a claim continua viva no banco apontando para um dono que não existe mais.
/// A partir daí, todo card do mesmo escopo colhe `workspace.scopeconflict` para sempre e o projeto
/// trava inteiro por causa de um processo morto.
///
/// A recuperação existia (com endpoint HTTP próprio), mas dependia de alguém chamá-la à mão. Aqui
/// ela roda sozinha: uma vez na subida — que é exatamente quando as órfãs do processo anterior
/// estão lá — e periodicamente, para cobrir lease vencida de execução que travou sem morrer.
/// </summary>
public sealed partial class AttemptRecoveryBackgroundService(
    IServiceScopeFactory scopes,
    AgentRunOrchestrator orchestrator,
    ILogger<AttemptRecoveryBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Recuperação: {Count} tentativa(s)/concessão(ões) órfã(s) devolvida(s) ao pool.")]
    private static partial void LogRecovered(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ciclo de recuperação de tentativas falhou: {ErrorType}")]
    private static partial void LogFailure(ILogger logger, string errorType);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var profiles = scope.ServiceProvider.GetRequiredService<ILocalProfileStore>();
                // Fase 1E: TODOS os tenants. Recuperar só o primeiro deixava tentativas órfãs
                // presas nos demais — e uma órfã presa mantém a claim de path viva, travando o
                // escopo daquele card para sempre. Um tenant com Host reiniciado paralisava o
                // projeto inteiro sem que nada no produto dissesse por quê.
                var list = await profiles.ListAsync(stoppingToken);
                var tenants = list
                    .Select(profile => profile.TenantId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                foreach (var tenantId in tenants)
                {
                    // Isolamento por iteração: um tenant com banco indisponível não pode impedir a
                    // recuperação dos outros.
                    try
                    {
                        var recovered = await orchestrator.RecoverAsync(tenantId, stoppingToken);
                        if (recovered.Count > 0)
                        {
                            LogRecovered(logger, recovered.Count);
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        LogFailure(logger, exception.GetType().Name);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Só o TIPO do erro — nunca conteúdo sensível. Uma falha não derruba o laço.
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
