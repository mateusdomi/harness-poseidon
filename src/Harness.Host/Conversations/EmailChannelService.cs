using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Harness.Host.Notifications;
using Harness.Host.Observability;
using Harness.Modules.Conversations.Application;
using Harness.Modules.Conversations.Contracts;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Conversations;

/// <summary>
/// Configuração do canal de e-mail conversacional. O <see cref="InboundToken"/> autentica o
/// webhook do provedor de recebimento e NUNCA é persistido nem logado: forneça-o apenas por
/// variável de ambiente (`Harness__Channels__Email__InboundToken`).
///
/// A SAÍDA reutiliza deliberadamente o relay já configurado em `Harness:Notifications:Smtp`
/// (referências opacas + <see cref="ISmtpTransport"/>): duas fontes de verdade para o mesmo
/// relay seriam defeito de canon.
/// </summary>
public sealed record EmailChannelOptions
{
    public string? InboundToken { get; init; }

    /// <summary>Assunto usado nas respostas da Bruna quando a thread não informa um.</summary>
    public string DefaultSubject { get; init; } = "Poseidon";

    public TimeSpan DeliveryInterval { get; init; } = TimeSpan.FromSeconds(5);

    public bool Enabled => !string.IsNullOrWhiteSpace(InboundToken);
}

/// <summary>
/// Adaptador de e-mail: entrada por webhook autenticado do provedor de recebimento (mesmo
/// contrato de envelope/dedup dos adapters Telegram, Teams e WhatsApp) e saída pelo relay SMTP
/// existente. Não há IMAP/POP: o Anexo B não autoriza dependência nova, e o padrão de webhook
/// já é o do Teams — convenção sobre invenção.
///
/// Anti-spoofing: além do token do webhook, o remetente precisa ter vínculo `email` no tenant;
/// a comparação de endereço é case-insensitive (domínios de e-mail não distinguem caixa).
/// </summary>
public sealed partial class EmailChannelBackgroundService(
    EmailChannelOptions options,
    SmtpNotificationOptions smtpOptions,
    ISmtpTransport transport,
    ISecretReferenceResolver secrets,
    IChannelLinkStore links,
    ILocalProfileStore profiles,
    IProjectStore projects,
    IChiefTurnStore chiefTurns,
    IConversationStore conversations,
    IClock clock,
    ILogger<EmailChannelBackgroundService> logger) : BackgroundService
{
    private const int MaximumBodyLength = 100_000;
    private readonly Dictionary<string, string> _lastDeliveredByLink = new(StringComparer.Ordinal);
    private bool _primed;

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The channel delivery worker must isolate transient provider failures without stopping the Host.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        LogEnabled(logger);
        using var timer = new PeriodicTimer(options.DeliveryInterval);
        try
        {
            do
            {
                try
                {
                    await DeliverRepliesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogDeliveryFailure(logger, exception.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento normal do Host.
        }
    }

    public async Task<EmailMessageReceipt> ReceiveAsync(
        string? authorization,
        EmailInboundMessage message,
        CancellationToken cancellationToken)
    {
        using var telemetry = new ChannelTelemetryScope("email", "inbound");
        try
        {
            if (!options.Enabled)
            {
                throw new EmailChannelUnavailableException("The email channel is not configured.");
            }

            ValidateAuthorization(authorization);
            ArgumentNullException.ThrowIfNull(message);
            var sender = message.From?.Trim();
            var externalMessageId = message.MessageId?.Trim();
            var body = message.Body?.Trim();
            if (string.IsNullOrWhiteSpace(sender) || sender.Length > 200 ||
                string.IsNullOrWhiteSpace(externalMessageId) || externalMessageId.Length > 200 ||
                string.IsNullOrWhiteSpace(body) || body.Length > MaximumBodyLength)
            {
                throw new EmailMessageValidationException("The email envelope is invalid.");
            }

            var content = string.IsNullOrWhiteSpace(message.Subject)
                ? body
                : $"{message.Subject.Trim()}\n\n{body}";

            var allProfiles = await profiles.ListAsync(cancellationToken);
            foreach (var tenantId in allProfiles.Select(profile => profile.TenantId)
                         .Distinct(StringComparer.Ordinal))
            {
                var link = (await links.ListAsync(tenantId, cancellationToken)).FirstOrDefault(candidate =>
                    candidate.Kind == "email" &&
                    string.Equals(candidate.ExternalIdentity, sender, StringComparison.OrdinalIgnoreCase));
                if (link is null)
                {
                    continue;
                }

                var turnId = ChannelEndpoints.DeterministicUlid(
                    $"channel:{link.Id}:email-message:{externalMessageId}");
                telemetry.SetCorrelation(
                    tenantId,
                    link.ProjectId,
                    link.ConversationId,
                    turnId,
                    link.Id);
                var project = await projects.GetAsync(tenantId, link.ProjectId, cancellationToken);
                if (project is null)
                {
                    telemetry.Complete("project_not_found");
                    return new EmailMessageReceipt(null, link.ConversationId, Linked: true, Deduplicated: false);
                }

                if (await chiefTurns.GetAsync(tenantId, turnId, cancellationToken) is not null)
                {
                    telemetry.Complete("deduplicated");
                    return new EmailMessageReceipt(turnId, link.ConversationId, Linked: true, Deduplicated: true);
                }

                var now = clock.UtcNow;
                var userAt = now.AddMilliseconds(1);
                var user = ConversationApplicationService.CreateUserMessage(
                    UlidValue.New(userAt).ToString(),
                    link.ProfileId,
                    new CreateMessageRequest(link.ConversationId, content),
                    userAt);
                await chiefTurns.EnqueueAsync(
                    new ChiefTurnEnqueueCommand(
                        tenantId,
                        link.ProjectId,
                        link.ConversationId,
                        turnId,
                        project.ChiefAgentId,
                        new MessageRecord(
                            tenantId,
                            link.ProjectId,
                            user.Id,
                            user.ConversationId,
                            user.AuthorRole,
                            user.AuthorProfileId,
                            user.AuthorAgentId,
                            user.Content,
                            user.TokenCount,
                            user.CreatedAt),
                        $"chief-turn:{turnId}",
                        now),
                    cancellationToken);
                await links.MarkInboundAsync(tenantId, link.Id, now, cancellationToken);
                telemetry.Complete("accepted");
                return new EmailMessageReceipt(turnId, link.ConversationId, Linked: true, Deduplicated: false);
            }

            // Remetente sem vínculo: nada é respondido por e-mail (evita amplificação/backscatter
            // para endereços forjados) e nenhum turno é criado.
            telemetry.Complete("unlinked");
            return new EmailMessageReceipt(null, null, Linked: false, Deduplicated: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.Complete("cancelled");
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            telemetry.Fail(exception);
            throw;
        }
    }

    public async Task DeliverRepliesAsync(CancellationToken cancellationToken)
    {
        var allProfiles = await profiles.ListAsync(cancellationToken);
        foreach (var tenantId in allProfiles.Select(profile => profile.TenantId)
                     .Distinct(StringComparer.Ordinal))
        {
            foreach (var link in (await links.ListAsync(tenantId, cancellationToken))
                         .Where(link => link.Kind == "email"))
            {
                var known = _lastDeliveredByLink.TryGetValue(link.Id, out var cursor);
                if (!known && !_primed)
                {
                    var latest = await conversations.ListMessagesAsync(
                        tenantId, link.ConversationId, null, 200, cancellationToken);
                    _lastDeliveredByLink[link.Id] = latest.Count > 0 ? latest[^1].Id : "";
                    continue;
                }

                var after = known && cursor!.Length > 0 ? cursor : null;
                var messages = await conversations.ListMessagesAsync(
                    tenantId, link.ConversationId, after, 200, cancellationToken);
                foreach (var message in messages)
                {
                    if (message.AuthorRole == "chief")
                    {
                        using var telemetry = new ChannelTelemetryScope("email", "outbound");
                        telemetry.SetCorrelation(
                            tenantId,
                            link.ProjectId,
                            link.ConversationId,
                            null,
                            link.Id,
                            message.Id);
                        try
                        {
                            if (await SendAsync(link.ExternalIdentity, message.Content, cancellationToken))
                            {
                                telemetry.Complete("delivered");
                            }
                            else
                            {
                                // Relay não configurado/irresolvível: no-op explícito e honesto,
                                // igual ao gateway de notificação — nunca inventa entrega.
                                telemetry.Complete("channel_unconfigured");
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            telemetry.Complete("cancelled");
                            throw;
                        }
                        catch (Exception exception) when (exception is not OutOfMemoryException)
                        {
                            telemetry.Fail(exception);
                            throw;
                        }
                    }

                    _lastDeliveredByLink[link.Id] = message.Id;
                }
            }
        }

        _primed = true;
    }

    /// <summary>
    /// Envia pelo relay SMTP existente. Retorna false quando o relay não está configurado ou
    /// suas referências opacas não resolvem — o chamador registra `channel_unconfigured`.
    /// </summary>
    private async Task<bool> SendAsync(string to, string body, CancellationToken cancellationToken)
    {
        if (!smtpOptions.IsConfigured)
        {
            return false;
        }

        var host = secrets.Resolve(smtpOptions.HostReference!);
        var from = secrets.Resolve(smtpOptions.FromAddressReference!);
        var password = secrets.Resolve(smtpOptions.PasswordReference!);
        if (string.IsNullOrWhiteSpace(host) ||
            string.IsNullOrWhiteSpace(from) ||
            string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        var username = string.IsNullOrWhiteSpace(smtpOptions.UsernameReference)
            ? null
            : secrets.Resolve(smtpOptions.UsernameReference);
        var port = smtpOptions.PortReference is null
            ? smtpOptions.DefaultPort
            : ResolvePort(secrets.Resolve(smtpOptions.PortReference));

        await transport.SendAsync(
            new SmtpEnvelope(
                host,
                port,
                smtpOptions.UseTls,
                username,
                password,
                from,
                to,
                options.DefaultSubject,
                body),
            cancellationToken);
        return true;
    }

    private int ResolvePort(string? resolved) =>
        int.TryParse(resolved, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) &&
        value is > 0 and <= 65535
            ? value
            : smtpOptions.DefaultPort;

    private void ValidateAuthorization(string? authorization)
    {
        const string prefix = "Bearer ";
        if (authorization?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new EmailMessageAuthenticationException("The inbound email is not authenticated.");
        }

        var supplied = authorization[prefix.Length..].Trim();
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.InboundToken!));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash))
        {
            throw new EmailMessageAuthenticationException("The inbound email is not authenticated.");
        }
    }

    [LoggerMessage(
        EventId = 5501,
        Level = LogLevel.Information,
        Message = "Canal de e-mail habilitado; entrega de respostas ativa.")]
    private static partial void LogEnabled(ILogger logger);

    [LoggerMessage(
        EventId = 5502,
        Level = LogLevel.Warning,
        Message = "Falha transitória na entrega de e-mail: {ErrorType}.")]
    private static partial void LogDeliveryFailure(ILogger logger, string errorType);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EmailInboundMessage(
    string? From,
    string? MessageId,
    string? Subject,
    string? Body);

public sealed record EmailMessageReceipt(
    string? TurnId,
    string? ConversationId,
    bool Linked,
    bool Deduplicated);

public sealed class EmailMessageAuthenticationException(string message) : Exception(message);
public sealed class EmailMessageValidationException(string message) : Exception(message);
public sealed class EmailChannelUnavailableException(string message) : Exception(message);
