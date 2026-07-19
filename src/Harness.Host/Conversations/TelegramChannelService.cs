using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
/// Configuração do canal Telegram. O token NUNCA é gravado em repositório,
/// documentação, banco ou logs: forneça-o exclusivamente por variável de
/// ambiente (`Harness__Channels__Telegram__BotToken`).
/// </summary>
public sealed record TelegramChannelOptions
{
    public string? BotToken { get; init; }

    public string ApiBaseUrl { get; init; } = "https://api.telegram.org";

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public bool Enabled => !string.IsNullOrWhiteSpace(BotToken);
}

public sealed partial class TelegramChannelBackgroundService(
    TelegramChannelOptions options,
    IChannelLinkStore links,
    ILocalProfileStore profiles,
    IProjectStore projects,
    IChiefTurnStore chiefTurns,
    IConversationStore conversations,
    IClock clock,
    ILogger<TelegramChannelBackgroundService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, string> _lastDeliveredByLink = new(StringComparer.Ordinal);
    private long _offset;
    private bool _primed;

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "O poller de canal deve isolar falhas transitórias do provedor sem derrubar o Host.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        LogEnabled(logger);
        using var timer = new PeriodicTimer(options.PollInterval);
        do
        {
            try
            {
                await PollOnceAsync(stoppingToken);
                await DeliverRepliesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogPollFailure(logger, exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var url = $"{options.ApiBaseUrl}/bot{options.BotToken}/getUpdates?timeout=0&offset={_offset}";
        using var response = await _http.GetAsync(new Uri(url), cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TelegramUpdatesEnvelope>(
            JsonOptions, cancellationToken);
        if (payload?.Result is not { Count: > 0 })
        {
            return;
        }

        foreach (var update in payload.Result)
        {
            _offset = Math.Max(_offset, update.UpdateId + 1);
            if (update.Message is null ||
                string.IsNullOrWhiteSpace(update.Message.Text) ||
                update.Message.Chat is null)
            {
                continue;
            }

            await HandleMessageAsync(update, cancellationToken);
        }
    }

    private async Task HandleMessageAsync(TelegramUpdate update, CancellationToken cancellationToken)
    {
        var chatId = update.Message!.Chat!.Id.ToString(CultureInfo.InvariantCulture);
        var allProfiles = await profiles.ListAsync(cancellationToken);
        foreach (var tenantId in allProfiles.Select(profile => profile.TenantId)
                     .Distinct(StringComparer.Ordinal))
        {
            var link = (await links.ListAsync(tenantId, cancellationToken)).FirstOrDefault(candidate =>
                candidate.Kind == "telegram" &&
                string.Equals(candidate.ExternalIdentity, chatId, StringComparison.Ordinal));
            if (link is null)
            {
                continue;
            }

            var project = await projects.GetAsync(tenantId, link.ProjectId, cancellationToken);
            if (project is null)
            {
                return;
            }

            var turnId = ChannelEndpoints.DeterministicUlid(
                $"channel:{link.Id}:telegram-update:{update.UpdateId}");
            if (await chiefTurns.GetAsync(tenantId, turnId, cancellationToken) is not null)
            {
                return;
            }

            var now = clock.UtcNow;
            var userAt = now.AddMilliseconds(1);
            var user = ConversationApplicationService.CreateUserMessage(
                UlidValue.New(userAt).ToString(),
                link.ProfileId,
                new CreateMessageRequest(link.ConversationId, update.Message.Text!),
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
            return;
        }

        await SendMessageAsync(
            chatId,
            "Esta conversa ainda não está vinculada ao Harness. Vincule a identidade " +
            $"'{chatId}' na tela de canais do aplicativo.",
            cancellationToken);
    }

    public async Task DeliverRepliesAsync(CancellationToken cancellationToken)
    {
        var allProfiles = await profiles.ListAsync(cancellationToken);
        foreach (var tenantId in allProfiles.Select(profile => profile.TenantId)
                     .Distinct(StringComparer.Ordinal))
        {
            foreach (var link in (await links.ListAsync(tenantId, cancellationToken))
                         .Where(link => link.Kind == "telegram"))
            {
                var known = _lastDeliveredByLink.TryGetValue(link.Id, out var cursor);
                if (!known && !_primed)
                {
                    // Primeiro ciclo após o start: não reentrega histórico anterior ao boot;
                    // links sem histórico entregam tudo a partir daqui.
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
                        await SendMessageAsync(link.ExternalIdentity, message.Content, cancellationToken);
                    }

                    _lastDeliveredByLink[link.Id] = message.Id;
                }
            }
        }

        _primed = true;
    }

    private async Task SendMessageAsync(string chatId, string text, CancellationToken cancellationToken)
    {
        var url = $"{options.ApiBaseUrl}/bot{options.BotToken}/sendMessage";
        using var response = await _http.PostAsJsonAsync(
            new Uri(url),
            new TelegramSendMessageRequest(chatId, text),
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    [LoggerMessage(
        EventId = 5101,
        Level = LogLevel.Information,
        Message = "Canal Telegram habilitado; polling de updates ativo.")]
    private static partial void LogEnabled(ILogger logger);

    [LoggerMessage(
        EventId = 5102,
        Level = LogLevel.Warning,
        Message = "Falha transitória no polling do Telegram: {ErrorType}.")]
    private static partial void LogPollFailure(ILogger logger, string errorType);
}

public sealed record TelegramUpdatesEnvelope(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("result")] IReadOnlyList<TelegramUpdate>? Result);

public sealed record TelegramUpdate(
    [property: JsonPropertyName("update_id")] long UpdateId,
    [property: JsonPropertyName("message")] TelegramMessage? Message);

public sealed record TelegramMessage(
    [property: JsonPropertyName("message_id")] long MessageId,
    [property: JsonPropertyName("chat")] TelegramChat? Chat,
    [property: JsonPropertyName("text")] string? Text);

public sealed record TelegramChat(
    [property: JsonPropertyName("id")] long Id);

public sealed record TelegramSendMessageRequest(
    [property: JsonPropertyName("chat_id")] string ChatId,
    [property: JsonPropertyName("text")] string Text);
