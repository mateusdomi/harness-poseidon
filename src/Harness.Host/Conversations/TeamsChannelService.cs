using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// Credenciais do adaptador Teams. InboundToken e OutboundToken devem ser fornecidos
/// exclusivamente pelo ambiente/cofre; nunca são persistidos nem incluídos em logs.
/// </summary>
public sealed record TeamsChannelOptions
{
    public string? InboundToken { get; init; }

    public string? OutboundToken { get; init; }

    public IReadOnlyList<string> AllowedServiceHosts { get; init; } =
        ["smba.trafficmanager.net"];

    public TimeSpan DeliveryInterval { get; init; } = TimeSpan.FromSeconds(2);

    public bool Enabled =>
        !string.IsNullOrWhiteSpace(InboundToken) && !string.IsNullOrWhiteSpace(OutboundToken);
}

public sealed partial class TeamsChannelBackgroundService(
    TeamsChannelOptions options,
    IChannelLinkStore links,
    ILocalProfileStore profiles,
    IProjectStore projects,
    IChiefTurnStore chiefTurns,
    IConversationStore conversations,
    IClock clock,
    ILogger<TeamsChannelBackgroundService> logger) : BackgroundService
{
    private const int MaximumTextLength = 28_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ConcurrentDictionary<string, TeamsReplyRoute> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _lastDeliveredByLink = new(StringComparer.Ordinal);
    private bool _primed;

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The channel delivery worker must isolate transient provider failures without stopping the Host.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
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

    public async Task<TeamsActivityReceipt> ReceiveAsync(
        string? authorization,
        TeamsActivity activity,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
            throw new TeamsChannelUnavailableException("The Teams channel is not configured.");
        ValidateAuthorization(authorization);
        ArgumentNullException.ThrowIfNull(activity);
        if (activity.Type != "message" || string.IsNullOrWhiteSpace(activity.Id) ||
            activity.Id.Length > 200 || activity.Conversation is null ||
            string.IsNullOrWhiteSpace(activity.Conversation.Id) || activity.From is null)
        {
            throw new TeamsActivityValidationException("The Teams activity envelope is invalid.");
        }
        var externalIdentity = string.IsNullOrWhiteSpace(activity.From.AadObjectId)
            ? activity.From.Id
            : activity.From.AadObjectId;
        if (string.IsNullOrWhiteSpace(externalIdentity) || externalIdentity.Length > 200)
            throw new TeamsActivityValidationException("The Teams sender identity is invalid.");
        var serviceUrl = ValidateServiceUrl(activity.ServiceUrl);
        var content = FormatContent(activity);
        var route = new TeamsReplyRoute(serviceUrl, activity.Conversation.Id, activity.Id);

        var allProfiles = await profiles.ListAsync(cancellationToken);
        foreach (var tenantId in allProfiles.Select(profile => profile.TenantId).Distinct(StringComparer.Ordinal))
        {
            var link = (await links.ListAsync(tenantId, cancellationToken)).FirstOrDefault(candidate =>
                candidate.Kind == "teams" &&
                string.Equals(candidate.ExternalIdentity, externalIdentity, StringComparison.Ordinal));
            if (link is null) continue;
            _routes[link.Id] = route;
            var project = await projects.GetAsync(tenantId, link.ProjectId, cancellationToken);
            if (project is null)
                throw new TeamsActivityValidationException("The linked Teams project no longer exists.");
            var turnId = ChannelEndpoints.DeterministicUlid(
                $"channel:{link.Id}:teams-activity:{activity.Id}");
            if (await chiefTurns.GetAsync(tenantId, turnId, cancellationToken) is not null)
                return new(turnId, link.ConversationId, Linked: true, Deduplicated: true);

            var now = clock.UtcNow;
            var user = ConversationApplicationService.CreateUserMessage(
                UlidValue.New(now.AddMilliseconds(1)).ToString(),
                link.ProfileId,
                new CreateMessageRequest(link.ConversationId, content),
                now.AddMilliseconds(1));
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
            return new(turnId, link.ConversationId, Linked: true, Deduplicated: false);
        }

        await SendTextAsync(
            route,
            "Esta identidade do Teams ainda não está vinculada ao Harness. " +
            $"Vincule '{externalIdentity}' na tela de canais do aplicativo.",
            cancellationToken);
        return new(null, null, Linked: false, Deduplicated: false);
    }

    public async Task DeliverRepliesAsync(CancellationToken cancellationToken)
    {
        var allProfiles = await profiles.ListAsync(cancellationToken);
        foreach (var tenantId in allProfiles.Select(profile => profile.TenantId).Distinct(StringComparer.Ordinal))
        {
            foreach (var link in (await links.ListAsync(tenantId, cancellationToken))
                         .Where(link => link.Kind == "teams"))
            {
                var known = _lastDeliveredByLink.TryGetValue(link.Id, out var cursor);
                if (!known && !_primed)
                {
                    var latest = await conversations.ListMessagesAsync(
                        tenantId, link.ConversationId, null, 200, cancellationToken);
                    _lastDeliveredByLink[link.Id] = latest.Count > 0 ? latest[^1].Id : "";
                    continue;
                }
                if (!_routes.TryGetValue(link.Id, out var route)) continue;
                var after = known && cursor!.Length > 0 ? cursor : null;
                var messages = await conversations.ListMessagesAsync(
                    tenantId, link.ConversationId, after, 200, cancellationToken);
                foreach (var message in messages)
                {
                    if (message.AuthorRole == "chief")
                    {
                        foreach (var chunk in Chunk(message.Content, MaximumTextLength))
                            await SendTextAsync(route, chunk, cancellationToken);
                    }
                    _lastDeliveredByLink[link.Id] = message.Id;
                }
            }
        }
        _primed = true;
    }

    private void ValidateAuthorization(string? authorization)
    {
        const string prefix = "Bearer ";
        if (authorization?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) != true)
            throw new TeamsActivityAuthenticationException("The Teams activity is not authenticated.");
        var supplied = authorization[prefix.Length..].Trim();
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.InboundToken!));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash))
            throw new TeamsActivityAuthenticationException("The Teams activity is not authenticated.");
    }

    private Uri ValidateServiceUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             !(uri.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address))) ||
            !options.AllowedServiceHosts.Any(host => string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase)))
        {
            throw new TeamsActivityValidationException("The Teams service URL is not allowlisted.");
        }
        var basePath = uri.AbsolutePath.TrimEnd('/') + "/";
        return new UriBuilder(uri) { Path = basePath, Query = "", Fragment = "" }.Uri;
    }

    private static string FormatContent(TeamsActivity activity)
    {
        if (activity.Attachments is { Count: > 10 })
            throw new TeamsActivityValidationException("The Teams activity contains too many attachments.");
        var builder = new StringBuilder(activity.Text?.Trim());
        foreach (var attachment in activity.Attachments ?? [])
        {
            if (string.IsNullOrWhiteSpace(attachment.Name) || attachment.Name.Length > 255 ||
                string.IsNullOrWhiteSpace(attachment.ContentType) || attachment.ContentType.Length > 200)
            {
                throw new TeamsActivityValidationException("Teams attachment metadata is invalid.");
            }
            if (builder.Length > 0) builder.AppendLine();
            builder.Append("[Anexo: ").Append(attachment.Name.Trim()).Append(" (")
                .Append(attachment.ContentType.Trim()).Append(")] ");
        }
        var content = builder.ToString().Trim();
        if (content.Length is < 1 or > MaximumTextLength)
            throw new TeamsActivityValidationException("The Teams message content length is invalid.");
        return content;
    }

    private async Task SendTextAsync(
        TeamsReplyRoute route,
        string text,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(
            route.ServiceUrl,
            $"v3/conversations/{Uri.EscapeDataString(route.ConversationId)}/activities");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.OutboundToken);
            request.Content = JsonContent.Create(
                new TeamsSendActivity("message", text, route.ActivityId),
                options: JsonOptions);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return;
            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500;
            if (!retryable || attempt == 3) response.EnsureSuccessStatusCode();
            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(100 * attempt);
            await Task.Delay(delay > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delay, cancellationToken);
        }
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var offset = 0; offset < text.Length; offset += size)
            yield return text.Substring(offset, Math.Min(size, text.Length - offset));
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    [LoggerMessage(EventId = 5201, Level = LogLevel.Information, Message = "Canal Teams habilitado; entrega de respostas ativa.")]
    private static partial void LogEnabled(ILogger logger);

    [LoggerMessage(EventId = 5202, Level = LogLevel.Warning, Message = "Falha transitória na entrega Teams: {ErrorType}.")]
    private static partial void LogDeliveryFailure(ILogger logger, string errorType);

    private sealed record TeamsReplyRoute(Uri ServiceUrl, string ConversationId, string ActivityId);
}

public sealed record TeamsActivity(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("serviceUrl")] string? ServiceUrl,
    [property: JsonPropertyName("from")] TeamsChannelAccount? From,
    [property: JsonPropertyName("conversation")] TeamsConversationAccount? Conversation,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("attachments")] IReadOnlyList<TeamsAttachment>? Attachments);

public sealed record TeamsChannelAccount(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("aadObjectId")] string? AadObjectId);

public sealed record TeamsConversationAccount([property: JsonPropertyName("id")] string Id);

public sealed record TeamsAttachment(
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("name")] string Name);

public sealed record TeamsSendActivity(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("replyToId")] string ReplyToId);

public sealed record TeamsActivityReceipt(
    string? TurnId,
    string? ConversationId,
    bool Linked,
    bool Deduplicated);

public sealed class TeamsActivityAuthenticationException(string message) : Exception(message);
public sealed class TeamsActivityValidationException(string message) : Exception(message);
public sealed class TeamsChannelUnavailableException(string message) : Exception(message);
