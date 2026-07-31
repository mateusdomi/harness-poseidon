using System.Text.Json;
using Harness.Host.Workers;
using Harness.Persistence.Abstractions.Messaging;

namespace Harness.Host.WorkBoard;

/// <summary>
/// Fase 0A1 (BR-004): o comando durável <c>plan.materializationRequested</c> é gravado na MESMA
/// transação que conclui o turno do Chefe, e é ESTE consumidor que o executa. O roteamento existe
/// porque a outbox do produto carrega dois tipos de mensagem: eventos que viram tempo real e
/// comandos internos que precisam de lease, fencing e retry — e um comando interno nunca deve ser
/// transmitido ao navegador.
///
/// Antes, o elo demanda→cards acontecia em memória depois do commit do turno: uma queda ali deixava
/// a demanda sem plano e sem cards, para sempre, sem que nada no produto soubesse.
/// </summary>
public sealed class PlanMaterializationOutboxSink(
    PlanMaterializationService materialization,
    IOutboxMessageSink inner,
    OutboxDispatcherOptions options,
    ILogger<PlanMaterializationOutboxSink> logger) : IOutboxMessageSink
{
    public const string EventType = "plan.materializationRequested";

    private static readonly Action<ILogger, string, string, Exception?> Outcome =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2112, nameof(Outcome)),
            "Plan materialization command for demand {DemandId} finished as {Result}.");

    private readonly PlanMaterializationService _materialization =
        materialization ?? throw new ArgumentNullException(nameof(materialization));
    private readonly IOutboxMessageSink _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly OutboxDispatcherOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<PlanMaterializationOutboxSink> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task DispatchAsync(
        OutboxLease message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.Equals(message.EventType, EventType, StringComparison.Ordinal))
        {
            await _inner.DispatchAsync(message, cancellationToken);
            return;
        }

        var command = JsonSerializer.Deserialize<PlanMaterializationCommandPayload>(
            message.PayloadJson, JsonOptions);
        if (command is null || string.IsNullOrWhiteSpace(command.DemandId))
        {
            // Um comando ilegível não pode ser retentado até o dead letter fingindo trabalho: ele
            // é um defeito de gravação, e o erro precisa aparecer inteiro.
            throw new InvalidOperationException(
                "The plan materialization command payload does not carry a demand id.");
        }

        // O dono é o processo que despacha: o par (dono, tentativa) faz o fencing das escritas
        // finais do compromisso, de modo que um consumidor que perdeu a corrida não conclui nada.
        var outcome = await _materialization.RunAsync(
            message.TenantId, command.DemandId, _options.Owner, cancellationToken);
        Outcome(_logger, command.DemandId, outcome.Result.ToString(), null);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record PlanMaterializationCommandPayload(
        string? TenantId, string? ProjectId, string? DemandId, string? TurnId);
}
