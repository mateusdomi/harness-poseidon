using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// Credenciais do adaptador WhatsApp (Meta Cloud API). AccessToken, AppSecret e
/// VerifyToken NUNCA são persistidos nem incluídos em logs: forneça-os exclusivamente
/// por variável de ambiente (`Harness__Channels__WhatsApp__*`).
/// </summary>
public sealed record WhatsAppChannelOptions
{
    public string? AccessToken { get; init; }

    public string? PhoneNumberId { get; init; }

    public string? VerifyToken { get; init; }

    public string? AppSecret { get; init; }

    public string GraphApiBaseUrl { get; init; } = "https://graph.facebook.com";

    public string ApiVersion { get; init; } = "v21.0";

    public TimeSpan DeliveryInterval { get; init; } = TimeSpan.FromSeconds(2);

    public bool Enabled =>
        !string.IsNullOrWhiteSpace(AccessToken) &&
        !string.IsNullOrWhiteSpace(PhoneNumberId) &&
        !string.IsNullOrWhiteSpace(VerifyToken) &&
        !string.IsNullOrWhiteSpace(AppSecret);
}

/// <summary>
/// Adaptador WhatsApp: entrada via webhook (Meta Cloud API), saída por polling
/// periódico da caixa da conversa — mesmo contrato que Telegram/Teams
/// (`docs/contracts/channels.md`). Diferente do Telegram (polling) e do Teams
/// (rota por conversa), o WhatsApp não exige rota de resposta: o destino de saída
/// é sempre `link.ExternalIdentity` (o `wa_id` do contato), fixo por vínculo.
/// </summary>
public sealed partial class WhatsAppChannelBackgroundService(
    WhatsAppChannelOptions options,
    IChannelLinkStore links,
    ILocalProfileStore profiles,
    IProjectStore projects,
    IChiefTurnStore chiefTurns,
    IConversationStore conversations,
    IClock clock,
    ILogger<WhatsAppChannelBackgroundService> logger) : BackgroundService
{
    private const int MaximumTextLength = 4_096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
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

    /// <summary>
    /// Handshake de verificação do webhook (GET), exigido pela Meta Cloud API antes
    /// de ativar a subscrição: ecoa `hub.challenge` somente se `hub.verify_token`
    /// bater com o valor configurado.
    /// </summary>
    public bool TryVerifyHandshake(string? mode, string? verifyToken)
    {
        if (!options.Enabled || mode != "subscribe" || verifyToken is null)
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(options.VerifyToken!);
        var supplied = Encoding.UTF8.GetBytes(verifyToken);
        return expected.Length == supplied.Length &&
            CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    public async Task<WhatsAppWebhookReceipt> ReceiveAsync(
        string? signatureHeader,
        byte[] rawBody,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            throw new WhatsAppChannelUnavailableException("The WhatsApp channel is not configured.");
        }

        ValidateSignature(signatureHeader, rawBody);
        var envelope = Deserialize(rawBody);
        var receipts = new List<WhatsAppMessageReceipt>();
        foreach (var entry in envelope.Entry ?? [])
        {
            foreach (var change in entry.Changes ?? [])
            {
                foreach (var message in change.Value?.Messages ?? [])
                {
                    receipts.Add(await HandleMessageAsync(message, cancellationToken));
                }
            }
        }

        return new WhatsAppWebhookReceipt(receipts);
    }

    private async Task<WhatsAppMessageReceipt> HandleMessageAsync(
        WhatsAppMessage message,
        CancellationToken cancellationToken)
    {
        using var telemetry = new ChannelTelemetryScope("whatsapp", "inbound");
        try
        {
            if (string.IsNullOrWhiteSpace(message.From) || string.IsNullOrWhiteSpace(message.Id))
            {
                telemetry.Complete("invalid_envelope");
                return new WhatsAppMessageReceipt(null, null, false, false);
            }

            if (message.Type != "text" || string.IsNullOrWhiteSpace(message.Text?.Body))
            {
                telemetry.Complete("unsupported_type");
                return new WhatsAppMessageReceipt(null, null, false, false);
            }

            var waId = message.From;
            var allProfiles = await profiles.ListAsync(cancellationToken);
            foreach (var tenantId in allProfiles.Select(profile => profile.TenantId)
                         .Distinct(StringComparer.Ordinal))
            {
                var link = (await links.ListAsync(tenantId, cancellationToken)).FirstOrDefault(candidate =>
                    candidate.Kind == "whatsapp" &&
                    string.Equals(candidate.ExternalIdentity, waId, StringComparison.Ordinal));
                if (link is null)
                {
                    continue;
                }

                var turnId = ChannelEndpoints.DeterministicUlid(
                    $"channel:{link.Id}:whatsapp-message:{message.Id}");
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
                    return new WhatsAppMessageReceipt(null, link.ConversationId, true, false);
                }

                if (await chiefTurns.GetAsync(tenantId, turnId, cancellationToken) is not null)
                {
                    telemetry.Complete("deduplicated");
                    return new WhatsAppMessageReceipt(turnId, link.ConversationId, true, true);
                }

                var now = clock.UtcNow;
                var userAt = now.AddMilliseconds(1);
                var user = ConversationApplicationService.CreateUserMessage(
                    UlidValue.New(userAt).ToString(),
                    link.ProfileId,
                    new CreateMessageRequest(link.ConversationId, message.Text.Body.Trim()),
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
                return new WhatsAppMessageReceipt(turnId, link.ConversationId, true, false);
            }

            await SendTextAsync(
                waId,
                "Este número ainda não está vinculado ao Harness. Vincule o WhatsApp " +
                $"'{waId}' na tela de canais do aplicativo.",
                cancellationToken);
            telemetry.Complete("unlinked");
            return new WhatsAppMessageReceipt(null, null, false, false);
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
                         .Where(link => link.Kind == "whatsapp"))
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
                        foreach (var chunk in Chunk(message.Content, MaximumTextLength))
                        {
                            using var telemetry = new ChannelTelemetryScope("whatsapp", "outbound");
                            telemetry.SetCorrelation(
                                tenantId,
                                link.ProjectId,
                                link.ConversationId,
                                null,
                                link.Id,
                                message.Id);
                            try
                            {
                                await SendTextAsync(link.ExternalIdentity, chunk, cancellationToken);
                                telemetry.Complete("delivered");
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
                    }

                    _lastDeliveredByLink[link.Id] = message.Id;
                }
            }
        }

        _primed = true;
    }

    private void ValidateSignature(string? signatureHeader, byte[] rawBody)
    {
        const string prefix = "sha256=";
        if (signatureHeader?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new WhatsAppWebhookAuthenticationException("The WhatsApp webhook signature is missing.");
        }

        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(signatureHeader[prefix.Length..]);
        }
        catch (FormatException)
        {
            throw new WhatsAppWebhookAuthenticationException("The WhatsApp webhook signature is malformed.");
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.AppSecret!), rawBody);
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            throw new WhatsAppWebhookAuthenticationException("The WhatsApp webhook signature is invalid.");
        }
    }

    private static WhatsAppWebhookEnvelope Deserialize(byte[] rawBody)
    {
        WhatsAppWebhookEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<WhatsAppWebhookEnvelope>(rawBody, JsonOptions);
        }
        catch (JsonException)
        {
            // A mensagem do provedor não é reproduzida: o corpo pode conter dados do
            // contato e o detalhe do problem+json vai para o cliente do webhook.
            throw new WhatsAppWebhookValidationException("The WhatsApp webhook payload is not valid JSON.");
        }

        if (envelope is null || envelope.ObjectKind != "whatsapp_business_account")
        {
            throw new WhatsAppWebhookValidationException("The WhatsApp webhook envelope is invalid.");
        }

        return envelope;
    }

    private async Task SendTextAsync(string to, string text, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(
            $"{options.GraphApiBaseUrl}/{options.ApiVersion}/{options.PhoneNumberId}/messages");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
            request.Content = JsonContent.Create(
                new WhatsAppSendMessageRequest("whatsapp", "individual", to, "text", new WhatsAppSendText(text)),
                options: JsonOptions);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500;
            if (!retryable || attempt == 3)
            {
                response.EnsureSuccessStatusCode();
            }

            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(100 * attempt);
            await Task.Delay(delay > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delay, cancellationToken);
        }
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var offset = 0; offset < text.Length; offset += size)
        {
            yield return text.Substring(offset, Math.Min(size, text.Length - offset));
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    [LoggerMessage(
        EventId = 5401,
        Level = LogLevel.Information,
        Message = "Canal WhatsApp habilitado; entrega de respostas ativa.")]
    private static partial void LogEnabled(ILogger logger);

    [LoggerMessage(
        EventId = 5402,
        Level = LogLevel.Warning,
        Message = "Falha transitória na entrega WhatsApp: {ErrorType}.")]
    private static partial void LogDeliveryFailure(ILogger logger, string errorType);
}

public sealed record WhatsAppWebhookEnvelope(
    [property: JsonPropertyName("object")] string? ObjectKind,
    [property: JsonPropertyName("entry")] IReadOnlyList<WhatsAppEntry>? Entry);

public sealed record WhatsAppEntry(
    [property: JsonPropertyName("changes")] IReadOnlyList<WhatsAppChange>? Changes);

public sealed record WhatsAppChange(
    [property: JsonPropertyName("value")] WhatsAppChangeValue? Value);

public sealed record WhatsAppChangeValue(
    [property: JsonPropertyName("messages")] IReadOnlyList<WhatsAppMessage>? Messages);

public sealed record WhatsAppMessage(
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] WhatsAppMessageText? Text);

public sealed record WhatsAppMessageText(
    [property: JsonPropertyName("body")] string? Body);

public sealed record WhatsAppSendMessageRequest(
    [property: JsonPropertyName("messaging_product")] string MessagingProduct,
    [property: JsonPropertyName("recipient_type")] string RecipientType,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] WhatsAppSendText Text);

public sealed record WhatsAppSendText(
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("preview_url")] bool PreviewUrl = false);

public sealed record WhatsAppMessageReceipt(
    string? TurnId,
    string? ConversationId,
    bool Linked,
    bool Deduplicated);

public sealed record WhatsAppWebhookReceipt(IReadOnlyList<WhatsAppMessageReceipt> Messages);

public sealed class WhatsAppWebhookAuthenticationException(string message) : Exception(message);
public sealed class WhatsAppWebhookValidationException(string message) : Exception(message);
public sealed class WhatsAppChannelUnavailableException(string message) : Exception(message);
