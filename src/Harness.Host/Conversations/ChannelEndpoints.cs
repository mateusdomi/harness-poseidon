using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Harness.Host.Profiles;
using Harness.Modules.Conversations.Application;
using Harness.Modules.Conversations.Contracts;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Conversations;

public static class ChannelEndpoints
{
    private static readonly HashSet<string> Kinds =
        new(["terminal", "telegram", "teams"], StringComparer.Ordinal);

    public static IEndpointRouteBuilder MapChannels(this IEndpointRouteBuilder endpoints)
    {
        var links = endpoints.MapGroup("/api/v1/channels/links").WithTags("channels");
        links.MapGet("/", ListLinksAsync)
            .Produces<ChannelLinkPage>()
            .ProducesProblem(401);
        links.MapPost("/", CreateLinkAsync)
            .Produces<ChannelLinkContract>(201)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        links.MapPost("/{linkId}/messages", SendMessageAsync)
            .Produces<ChannelMessageReceipt>(202)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        links.MapGet("/{linkId}/messages", ListMessagesAsync)
            .Produces<ChannelMessagePage>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        endpoints.MapPost("/api/v1/channels/teams/activities", ReceiveTeamsActivityAsync)
            .WithTags("channels")
            .Produces<TeamsActivityReceipt>(202)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(503);
        return endpoints;
    }

    private static async Task<IResult> ReceiveTeamsActivityAsync(
        TeamsActivity activity,
        HttpRequest request,
        TeamsChannelBackgroundService teams,
        CancellationToken token)
    {
        try
        {
            var receipt = await teams.ReceiveAsync(request.Headers.Authorization, activity, token);
            return Results.Accepted(value: receipt);
        }
        catch (TeamsActivityAuthenticationException exception)
        {
            return Problem(401, "teams_activity_unauthorized", exception.Message);
        }
        catch (TeamsActivityValidationException exception)
        {
            return Problem(400, "invalid_teams_activity", exception.Message);
        }
        catch (TeamsChannelUnavailableException exception)
        {
            return Problem(503, "teams_channel_unavailable", exception.Message);
        }
    }

    private static async Task<IResult> ListLinksAsync(
        HttpRequest request,
        ILocalProfileStore profiles,
        IChannelLinkStore store,
        CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return SessionRequired();
        }

        var items = await store.ListAsync(profile.TenantId, token);
        return Results.Ok(new ChannelLinkPage(items.Select(ToContract).ToArray(), null));
    }

    private static async Task<IResult> CreateLinkAsync(
        CreateChannelLinkRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IConversationStore conversations,
        IChannelLinkStore store,
        IAuditEventStore audit,
        IClock clock,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Kinds.Contains(input.Kind))
        {
            return Problem(400, "invalid_channel_kind", "O tipo de canal está fora do conjunto fechado.");
        }

        if (string.IsNullOrWhiteSpace(input.ExternalIdentity) || input.ExternalIdentity.Length > 200)
        {
            return Problem(
                400,
                "invalid_external_identity",
                "A identidade externa é obrigatória e limitada a 200 caracteres.");
        }

        if (!UlidValue.TryParse(input.ProjectId, out _))
        {
            return Problem(400, "invalid_project_id", "Project ID must be a ULID.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return SessionRequired();
        }

        var project = await projects.GetAsync(profile.TenantId, input.ProjectId, token);
        if (project is null)
        {
            return Problem(404, "project_not_found", "The requested resource does not exist.");
        }

        var identity = input.ExternalIdentity.Trim();
        var alreadyLinked = (await store.ListAsync(profile.TenantId, token)).FirstOrDefault(link =>
            link.Kind == input.Kind &&
            string.Equals(link.ExternalIdentity, identity, StringComparison.Ordinal));
        if (alreadyLinked is not null)
        {
            return Results.Created(
                $"/api/v1/channels/links/{alreadyLinked.Id}",
                ToContract(alreadyLinked));
        }

        var now = clock.UtcNow;
        var contract = ConversationApplicationService.Create(
            UlidValue.New(now).ToString(),
            profile.Id,
            new CreateConversationRequest(
                project.Id,
                $"Canal {input.Kind}: {input.ExternalIdentity.Trim()}"),
            now);
        var conversation = await conversations.CreateConversationAsync(
            new ConversationCreateCommand(
                new ConversationRecord(
                    profile.TenantId,
                    contract.Id,
                    contract.ProjectId,
                    contract.Title,
                    contract.State,
                    contract.CreatedByProfileId,
                    contract.CreatedAt,
                    contract.LastMessageAt,
                    contract.Version),
                now),
            token);
        if (conversation.Status != ConversationMutationStatus.Applied)
        {
            return Problem(400, "channel_conversation_failed", "A conversa do canal não pôde ser criada.");
        }

        var link = await store.GetOrCreateAsync(
            new ChannelLinkCreateCommand(
                profile.TenantId,
                UlidValue.New(now.AddMilliseconds(1)).ToString(),
                input.Kind,
                input.ExternalIdentity.Trim(),
                profile.Id,
                project.Id,
                contract.Id,
                now),
            token);
        if (link.ConversationId == contract.Id)
        {
            await audit.AppendAsync(
                new AuditEventAppendCommand(
                    profile.TenantId,
                    "user",
                    profile.Id,
                    "channel.linked",
                    "channel_link",
                    link.Id,
                    $"{input.Kind}:{input.ExternalIdentity.Trim()} vinculado ao projeto {project.Id}.",
                    now),
                token);
        }

        return Results.Created($"/api/v1/channels/links/{link.Id}", ToContract(link));
    }

    private static async Task<IResult> SendMessageAsync(
        string linkId,
        ChannelMessageRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IChannelLinkStore store,
        IProjectStore projects,
        IChiefTurnStore chiefTurns,
        IClock clock,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!UlidValue.TryParse(linkId, out _))
        {
            return Problem(400, "invalid_link_id", "Link ID must be a ULID.");
        }

        if (string.IsNullOrWhiteSpace(input.ExternalMessageId) || input.ExternalMessageId.Length > 200)
        {
            return Problem(
                400,
                "invalid_external_message_id",
                "O ID externo da mensagem é obrigatório e limitado a 200 caracteres.");
        }

        if (string.IsNullOrWhiteSpace(input.Content))
        {
            return Problem(400, "invalid_channel_message", "O conteúdo da mensagem é obrigatório.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return SessionRequired();
        }

        var link = await store.GetAsync(profile.TenantId, linkId, token);
        if (link is null)
        {
            return Problem(404, "channel_link_not_found", "The requested resource does not exist.");
        }

        var project = await projects.GetAsync(profile.TenantId, link.ProjectId, token);
        if (project is null)
        {
            return Problem(404, "project_not_found", "The requested resource does not exist.");
        }

        // O turn id é derivado do id externo: a reentrega do provedor produz o MESMO
        // turno e o Inbox durável garante zero duplicação de mensagem/execução.
        var turnId = DeterministicUlid($"channel:{link.Id}:{input.ExternalMessageId.Trim()}");
        var existing = await chiefTurns.GetAsync(profile.TenantId, turnId, token);
        if (existing is not null)
        {
            return Results.Accepted(
                value: new ChannelMessageReceipt(turnId, link.ConversationId, Deduplicated: true));
        }

        var now = clock.UtcNow;
        var userAt = now.AddMilliseconds(1);
        var user = ConversationApplicationService.CreateUserMessage(
            UlidValue.New(userAt).ToString(),
            link.ProfileId,
            new CreateMessageRequest(link.ConversationId, input.Content),
            userAt);
        try
        {
            await chiefTurns.EnqueueAsync(
                new ChiefTurnEnqueueCommand(
                    profile.TenantId,
                    link.ProjectId,
                    link.ConversationId,
                    turnId,
                    project.ChiefAgentId,
                    new MessageRecord(
                        profile.TenantId,
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
                token);
        }
        catch (ArgumentException exception)
        {
            return Problem(400, "invalid_channel_message", exception.Message);
        }
        catch (ChiefTurnConflictException exception)
        {
            return Problem(400, "channel_conversation_inactive", exception.Message);
        }

        return Results.Accepted(
            value: new ChannelMessageReceipt(turnId, link.ConversationId, Deduplicated: false));
    }

    private static async Task<IResult> ListMessagesAsync(
        string linkId,
        string? afterMessageId,
        int? limit,
        HttpRequest request,
        ILocalProfileStore profiles,
        IChannelLinkStore store,
        IConversationStore conversations,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(linkId, out _) ||
            (afterMessageId is not null && !UlidValue.TryParse(afterMessageId, out _)) ||
            limit is < 1 or > 200)
        {
            return Problem(400, "invalid_channel_query", "Channel message query is invalid.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return SessionRequired();
        }

        var link = await store.GetAsync(profile.TenantId, linkId, token);
        if (link is null)
        {
            return Problem(404, "channel_link_not_found", "The requested resource does not exist.");
        }

        var records = await conversations.ListMessagesAsync(
            profile.TenantId,
            link.ConversationId,
            afterMessageId,
            limit ?? 50,
            token);
        return Results.Ok(new ChannelMessagePage(
            records
                .Select(message => new ChannelMessageContract(
                    message.Id,
                    message.AuthorRole,
                    message.Content,
                    message.CreatedAt))
                .ToArray(),
            records.Count == 0 ? afterMessageId : records[^1].Id));
    }

    internal static string DeterministicUlid(string seed)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var builder = new StringBuilder(26);
        for (var index = 0; index < 26; index++)
        {
            builder.Append(alphabet[hash[index] % alphabet.Length]);
        }

        // O primeiro caractere de um ULID canônico é limitado a 0-7.
        builder[0] = alphabet[hash[0] % 8];
        return builder.ToString();
    }

    private static ChannelLinkContract ToContract(ChannelLinkRecord value) => new(
        value.Id,
        value.Kind,
        value.ExternalIdentity,
        value.ProjectId,
        value.ConversationId,
        value.LinkedAt);

    private static IResult SessionRequired() =>
        Problem(401, "local_session_required", "A local profile session is required.");

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateChannelLinkRequest(
    string Kind,
    string ExternalIdentity,
    string ProjectId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChannelMessageRequest(
    string ExternalMessageId,
    string Content);

public sealed record ChannelLinkContract(
    string Id,
    string Kind,
    string ExternalIdentity,
    string ProjectId,
    string ConversationId,
    DateTimeOffset LinkedAt);

public sealed record ChannelLinkPage(
    IReadOnlyList<ChannelLinkContract> Items,
    string? NextCursor);

public sealed record ChannelMessageReceipt(
    string TurnId,
    string ConversationId,
    bool Deduplicated);

public sealed record ChannelMessageContract(
    string Id,
    string AuthorRole,
    string Content,
    DateTimeOffset CreatedAt);

public sealed record ChannelMessagePage(
    IReadOnlyList<ChannelMessageContract> Items,
    string? NextCursor);
