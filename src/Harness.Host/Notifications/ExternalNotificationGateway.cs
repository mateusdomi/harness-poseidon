using Harness.SharedKernel.Security;

namespace Harness.Host.Notifications;

/// <summary>
/// Um pedido de notificação a caminho de um canal externo. O destinatário é uma REFERÊNCIA
/// OPACA (`env://`, `secret://`, `keychain://`) — o gateway resolve o endereço real só no
/// despacho e nunca o persiste nem loga. Assunto e corpo já vêm renderizados pelo emissor
/// (ex.: o Chefe notificando "bloqueado"/"aguardando revisão"/"concluído").
/// </summary>
public sealed record NotificationDispatch(
    string Channel,
    string RecipientReference,
    string Subject,
    string Body);

/// <summary>
/// Gateway de notificações externas: mantém os adaptadores de canal registrados e roteia um
/// <see cref="NotificationDispatch"/> ao canal cujo nome corresponde ao destino. Espelha a
/// forma dos adaptadores Telegram/Teams — um canal ausente ou não configurado é um NO-OP
/// explícito (resultado <see cref="NotificationDeliveryResult.Skipped"/>), jamais uma exceção
/// que derrube o emissor. Nenhum endereço ou segredo entra em log.
/// </summary>
public sealed partial class ExternalNotificationGateway
{
    private readonly IReadOnlyDictionary<string, IExternalNotificationChannel> _channels;
    private readonly ISecretReferenceResolver _secrets;
    private readonly ILogger<ExternalNotificationGateway> _logger;

    public ExternalNotificationGateway(
        IEnumerable<IExternalNotificationChannel> channels,
        ISecretReferenceResolver secrets,
        ILogger<ExternalNotificationGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(channels);
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channels = channels.ToDictionary(
            channel => channel.Channel,
            channel => channel,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Nomes dos canais atualmente configurados (para diagnóstico, sem segredo).</summary>
    public IReadOnlyCollection<string> ConfiguredChannels =>
        [.. _channels.Values.Where(channel => channel.IsConfigured).Select(channel => channel.Channel)];

    public async Task<NotificationDeliveryResult> DispatchAsync(
        NotificationDispatch dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (!_channels.TryGetValue(dispatch.Channel, out var channel) || !channel.IsConfigured)
        {
            // Canal não registrado ou desligado por padrão: no-op silencioso, sem mudar
            // comportamento de deploy que não configurou o canal.
            return NotificationDeliveryResult.SkippedUnconfigured();
        }

        var recipient = _secrets.Resolve(dispatch.RecipientReference);
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return NotificationDeliveryResult.Failed("recipient_unresolved");
        }

        var result = await channel.SendAsync(
            new RenderedNotification(recipient, dispatch.Subject, dispatch.Body),
            cancellationToken);
        if (!result.Delivered && !result.Skipped)
        {
            LogDispatchFailure(_logger, dispatch.Channel, result.FailureReason ?? "unknown");
        }

        return result;
    }

    /// <summary>
    /// Recusa um destinatário literal: como todo valor sensível, o destinatário viaja por
    /// referência opaca. Útil para o emissor validar antes de despachar.
    /// </summary>
    public static void EnsureOpaqueRecipient(string recipientReference)
    {
        if (string.IsNullOrWhiteSpace(recipientReference) ||
            recipientReference.IndexOf("://", StringComparison.Ordinal) <= 0 ||
            SecretTextProtector.ContainsSecret(recipientReference))
        {
            throw new ArgumentException(
                "O destinatário da notificação precisa ser uma referência opaca de segredo.",
                nameof(recipientReference));
        }
    }

    [LoggerMessage(
        EventId = 5310,
        Level = LogLevel.Warning,
        Message = "Falha ao despachar notificação pelo canal {Channel}: {Reason}.")]
    private static partial void LogDispatchFailure(ILogger logger, string channel, string reason);
}
