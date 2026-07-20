using Harness.Host.Profiles;
using Harness.Modules.Conversations.Application;
using Harness.Modules.Conversations.Contracts;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Conversations;

public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversations(this IEndpointRouteBuilder endpoints)
    {
        var conversations = endpoints.MapGroup("/api/v1/conversations").WithTags("conversations");
        conversations.MapGet("/", ListConversationsAsync)
            .Produces<ConversationPage>().ProducesProblem(400).ProducesProblem(401);
        conversations.MapGet("/{conversationId}", GetConversationAsync)
            .Produces<ConversationResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        conversations.MapPost("/", CreateConversationAsync)
            .Produces<ConversationResponse>(201).ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(404).ProducesProblem(409);
        conversations.MapDelete("/{conversationId}", DeleteConversationAsync)
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404)
            .ProducesProblem(409);
        conversations.MapPost("/{conversationId}/turns", StartTurnAsync)
            .Produces<ChatTurnHandle>(202).ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(404).ProducesProblem(409);

        var messages = endpoints.MapGroup("/api/v1/messages").WithTags("messages");
        messages.MapGet("/", ListMessagesAsync)
            .Produces<MessagePage>().ProducesProblem(400).ProducesProblem(401);
        messages.MapGet("/{messageId}", GetMessageAsync)
            .Produces<MessageResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        messages.MapPost("/", CreateMessageAsync)
            .Produces<MessageResponse>(201).ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(404).ProducesProblem(409);
        return endpoints;
    }

    private static async Task<IResult> ListConversationsAsync(
        string? projectId,
        string? cursor,
        int? limit,
        HttpRequest request,
        ILocalProfileStore profiles,
        IConversationStore store,
        CancellationToken cancellationToken)
    {
        var invalid = ValidatePage(projectId, cursor, limit);
        if (invalid is not null) return invalid;
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null) return SessionRequired();
        var size = limit ?? 50;
        var records = await store.ListConversationsAsync(
            profile.TenantId, projectId, cursor, size + 1, cancellationToken);
        var more = records.Count > size;
        var items = records.Take(size).Select(ToResponse).ToArray();
        return Results.Ok(new ConversationPage(items, more ? items[^1].Id : null));
    }

    private static async Task<IResult> GetConversationAsync(
        string conversationId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IConversationStore store,
        CancellationToken cancellationToken)
    {
        if (!UlidValue.TryParse(conversationId, out _)) return InvalidConversationId();
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null) return SessionRequired();
        var value = await store.GetConversationAsync(
            profile.TenantId, conversationId, cancellationToken);
        return value is null ? ConversationNotFound() : Results.Ok(ToResponse(value));
    }

    private static async Task<IResult> CreateConversationAsync(
        CreateConversationRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IConversationStore store,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null) return SessionRequired();
        try
        {
            var now = clock.UtcNow;
            var contract = ConversationApplicationService.Create(
                UlidValue.New(now).ToString(), profile.Id, input, now);
            var result = await store.CreateConversationAsync(
                new ConversationCreateCommand(ToRecord(profile.TenantId, contract), now),
                cancellationToken);
            return result.Status switch
            {
                ConversationMutationStatus.Applied => Results.Created(
                    $"/api/v1/conversations/{contract.Id}", ToResponse(result.Conversation!)),
                ConversationMutationStatus.ProjectNotFound => ProjectNotFound(),
                ConversationMutationStatus.AlreadyExists => Problem(
                    409, "conversation_already_exists", "The conversation already exists."),
                _ => throw new InvalidOperationException(
                    $"Unexpected conversation create status {result.Status}."),
            };
        }
        catch (ArgumentException exception)
        {
            return Problem(400, "invalid_conversation", exception.Message);
        }
    }

    private static async Task<IResult> DeleteConversationAsync(
        string conversationId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IConversationStore store,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!UlidValue.TryParse(conversationId, out _)) return InvalidConversationId();
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null) return SessionRequired();
        var current = await store.GetConversationAsync(
            profile.TenantId, conversationId, cancellationToken);
        if (current is null) return ConversationNotFound();
        var result = await store.DeleteConversationAsync(
            profile.TenantId, conversationId, current.Version, clock.UtcNow, cancellationToken);
        return result.Status switch
        {
            ConversationMutationStatus.Applied => Results.NoContent(),
            ConversationMutationStatus.NotFound => ConversationNotFound(),
            ConversationMutationStatus.Inactive => Problem(
                409, "conversation_inactive", "The conversation is not active."),
            ConversationMutationStatus.VersionConflict => Problem(
                409, "conversation_version_conflict", "The conversation changed concurrently."),
            _ => throw new InvalidOperationException(
                $"Unexpected conversation delete status {result.Status}."),
        };
    }

    private static async Task<IResult> ListMessagesAsync(
        string? conversationId,
        string? cursor,
        int? limit,
        HttpRequest request,
        ILocalProfileStore profiles,
        IConversationStore store,
        CancellationToken cancellationToken)
    {
        var invalid = ValidatePage(conversationId, cursor, limit);
        if (invalid is not null) return invalid;
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null) return SessionRequired();
        var size = limit ?? 100;
        var records = await store.ListMessagesAsync(
            profile.TenantId, conversationId, cursor, size + 1, cancellationToken);
        var more = records.Count > size;
        var items = records.Take(size).Select(ToResponse).ToArray();
        return Results.Ok(new MessagePage(items, more ? items[^1].Id : null));
    }

    private static async Task<IResult> GetMessageAsync(
        string messageId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IConversationStore store,
        CancellationToken cancellationToken)
    {
        if (!UlidValue.TryParse(messageId, out _)) return InvalidMessageId();
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null) return SessionRequired();
        var value = await store.GetMessageAsync(profile.TenantId, messageId, cancellationToken);
        return value is null ? MessageNotFound() : Results.Ok(ToResponse(value));
    }

    private static async Task<IResult> CreateMessageAsync(
        CreateMessageRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IConversationStore store,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null) return SessionRequired();
        var conversation = await store.GetConversationAsync(
            profile.TenantId, input.ConversationId, cancellationToken);
        if (conversation is null) return ConversationNotFound();
        try
        {
            var now = clock.UtcNow;
            var message = ConversationApplicationService.CreateUserMessage(
                UlidValue.New(now).ToString(), profile.Id, input, now);
            var record = ToRecord(profile.TenantId, conversation.ProjectId, message);
            var result = await store.CreateMessageAsync(
                new MessageCreateCommand(profile.TenantId, record, now), cancellationToken);
            return result.Status switch
            {
                MessageMutationStatus.Applied => Results.Created(
                    $"/api/v1/messages/{record.Id}", ToResponse(result.Message!)),
                MessageMutationStatus.ConversationNotFound => ConversationNotFound(),
                MessageMutationStatus.ConversationInactive => Problem(
                    409, "conversation_inactive", "The conversation is not active."),
                MessageMutationStatus.AlreadyExists => Problem(
                    409, "message_already_exists", "The message already exists."),
                _ => throw new InvalidOperationException(
                    $"Unexpected message create status {result.Status}."),
            };
        }
        catch (ArgumentException exception)
        {
            return Problem(400, "invalid_message", exception.Message);
        }
    }

    private static async Task<IResult> StartTurnAsync(
        string conversationId,
        StartChatTurnRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IConversationStore conversations,
        IChiefTurnStore chiefTurns,
        IProjectStore projects,
        ChiefInvocationRoutingService routing,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!UlidValue.TryParse(conversationId, out _)) return InvalidConversationId();
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null) return SessionRequired();
        var conversation = await conversations.GetConversationAsync(
            profile.TenantId, conversationId, cancellationToken);
        if (conversation is null) return ConversationNotFound();
        var project = await projects.GetAsync(
            profile.TenantId, conversation.ProjectId, cancellationToken);
        if (project is null) return ProjectNotFound();
        try
        {
            var now = clock.UtcNow;
            var turnId = UlidValue.New(now).ToString();
            var userAt = now.AddMilliseconds(1);
            var user = ConversationApplicationService.CreateUserMessage(
                UlidValue.New(userAt).ToString(), profile.Id,
                new CreateMessageRequest(conversationId, input.Content), userAt);
            var selection = await routing.ResolveAsync(
                profile.TenantId, project.ChiefAgentId, input, cancellationToken);
            await chiefTurns.EnqueueAsync(
                new ChiefTurnEnqueueCommand(
                    profile.TenantId,
                    project.Id,
                    conversationId,
                    turnId,
                    project.ChiefAgentId,
                    ToRecord(profile.TenantId, conversation.ProjectId, user),
                    $"chief-turn:{turnId}",
                    now,
                    selection),
                cancellationToken);
            return Results.Accepted(value: new ChatTurnHandle(turnId, conversationId));
        }
        catch (ArgumentException exception)
        {
            return Problem(400, "invalid_chat_turn", exception.Message);
        }
        catch (ChiefTurnConflictException exception)
        {
            return Problem(409, "chief_turn_conflict", exception.Message);
        }
        catch (ChiefInvocationSelectionException exception)
        {
            return Problem(400, "invalid_chief_invocation_selection", exception.Message);
        }
    }

    private static IResult? ValidatePage(string? filterId, string? cursor, int? limit)
    {
        if ((filterId is not null && !UlidValue.TryParse(filterId, out _)) ||
            (cursor is not null && !UlidValue.TryParse(cursor, out _)) ||
            limit is < 1 or > 200)
        {
            return Problem(400, "invalid_cursor", "Filter, cursor, or limit is invalid.");
        }

        return null;
    }

    private static ConversationRecord ToRecord(string tenantId, ConversationContract value) =>
        new(tenantId, value.Id, value.ProjectId, value.Title, value.State,
            value.CreatedByProfileId, value.CreatedAt, value.LastMessageAt, value.Version);

    private static MessageRecord ToRecord(
        string tenantId,
        string projectId,
        MessageContract value) =>
        new(tenantId, projectId, value.Id, value.ConversationId, value.AuthorRole,
            value.AuthorProfileId, value.AuthorAgentId, value.Content, value.TokenCount, value.CreatedAt);

    private static ConversationResponse ToResponse(ConversationRecord value) =>
        new(value.Id, value.ProjectId, value.Title, value.State, value.CreatedByProfileId,
            value.CreatedAt, value.LastMessageAt);

    private static MessageResponse ToResponse(MessageRecord value) =>
        new(value.Id, value.ConversationId, value.AuthorRole, value.AuthorProfileId,
            value.AuthorAgentId, value.Content, value.TokenCount, value.CreatedAt);

    private static IResult InvalidConversationId() =>
        Problem(400, "invalid_conversation_id", "Conversation ID must be a ULID.");
    private static IResult InvalidMessageId() =>
        Problem(400, "invalid_message_id", "Message ID must be a ULID.");
    private static IResult SessionRequired() =>
        Problem(401, "local_session_required", "A local profile session is required.");
    private static IResult ConversationNotFound() =>
        Problem(404, "conversation_not_found", "The conversation does not exist.");
    private static IResult MessageNotFound() =>
        Problem(404, "message_not_found", "The message does not exist.");
    private static IResult ProjectNotFound() =>
        Problem(404, "project_not_found", "The project does not exist.");
    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);

}

public sealed record ConversationResponse(
    string Id,
    string ProjectId,
    string Title,
    string State,
    string CreatedByProfileId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt);

public sealed record MessageResponse(
    string Id,
    string ConversationId,
    string AuthorRole,
    string? AuthorProfileId,
    string? AuthorAgentId,
    string Content,
    int? TokenCount,
    DateTimeOffset CreatedAt);

public sealed record ConversationPage(IReadOnlyList<ConversationResponse> Items, string? NextCursor);
public sealed record MessagePage(IReadOnlyList<MessageResponse> Items, string? NextCursor);
