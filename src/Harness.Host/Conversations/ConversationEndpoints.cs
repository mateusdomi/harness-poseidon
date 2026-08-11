using Harness.Host.Profiles;
using Harness.Host.V3;
using Harness.Host.WorkBoard;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Conversations.Application;
using Harness.Modules.Conversations.Contracts;
using Harness.Modules.Conversations.Domain;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Prototyping;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;
using Harness.Modules.Readiness.Contracts;
using Microsoft.AspNetCore.Mvc;

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
        conversations.MapPatch("/{conversationId}", RenameConversationAsync)
            .Produces<ConversationResponse>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(404).ProducesProblem(409);
        conversations.MapDelete("/{conversationId}", DeleteConversationAsync)
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404)
            .ProducesProblem(409);
        // C4/ADR-019: a primeira conversa não pode depender de o usuário descobrir "Nova
        // conversa". Este comando é idempotente: devolve a conversa ativa mais antiga do
        // projeto ou cria a primeira. Criar conversa não é executar — permanece permitido
        // mesmo com a execução bloqueada.
        endpoints.MapPost("/api/v1/projects/{projectId}/conversations/primary", EnsurePrimaryAsync)
            .WithTags("conversations")
            .Produces<ConversationResponse>(200).Produces<ConversationResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        endpoints.MapGet("/api/v1/projects/{projectId}/conversations/active", RecallActiveAsync)
            .WithTags("conversations")
            .Produces<ActiveConversationResponse>().Produces(204)
            .ProducesProblem(400).ProducesProblem(401);
        endpoints.MapPut("/api/v1/projects/{projectId}/conversations/active", RememberActiveAsync)
            .WithTags("conversations")
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
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

    private static async Task<IResult> EnsurePrimaryAsync(
        string projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IConversationStore store,
        IProjectStore projects,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!UlidValue.TryParse(projectId, out _))
        {
            return Problem(400, "invalid_project_id", "Project ID must be a ULID.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null) return SessionRequired();
        if (await projects.GetAsync(profile.TenantId, projectId, cancellationToken) is null)
        {
            return ProjectNotFound();
        }

        // Idempotente: a conversa ativa mais antiga do projeto é a primária. Reexecutar o
        // comando devolve a mesma conversa, sem criar duplicata nem alterar o histórico.
        var existing = await store.ListConversationsAsync(
            profile.TenantId, projectId, null, 200, cancellationToken);
        var primary = existing
            .Where(item => item.State == "active")
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (primary is not null)
        {
            return Results.Ok(ToResponse(primary));
        }

        try
        {
            var now = clock.UtcNow;
            var contract = ConversationApplicationService.Create(
                UlidValue.New(now).ToString(), profile.Id,
                new CreateConversationRequest(projectId, "Conversa inicial"), now);
            var result = await store.CreateConversationAsync(
                new ConversationCreateCommand(ToRecord(profile.TenantId, contract), now),
                cancellationToken);
            return result.Status switch
            {
                ConversationMutationStatus.Applied => Results.Created(
                    $"/api/v1/conversations/{contract.Id}", ToResponse(result.Conversation!)),
                ConversationMutationStatus.ProjectNotFound => ProjectNotFound(),
                // Corrida com outra criação concorrente: reexecuta a leitura idempotente.
                ConversationMutationStatus.AlreadyExists => Results.Ok(ToResponse(
                    (await store.ListConversationsAsync(
                        profile.TenantId, projectId, null, 200, cancellationToken))
                    .Where(item => item.State == "active")
                    .OrderBy(item => item.Id, StringComparer.Ordinal)
                    .First())),
                _ => throw new InvalidOperationException(
                    $"Unexpected conversation create status {result.Status}."),
            };
        }
        catch (ArgumentException exception)
        {
            return Problem(400, "invalid_conversation", exception.Message);
        }
    }

    private static async Task<IResult> RecallActiveAsync(
        string projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProfileActiveConversationStore activeConversations,
        IConversationStore conversations,
        CancellationToken cancellationToken)
    {
        if (!UlidValue.TryParse(projectId, out _))
        {
            return Problem(400, "invalid_project_id", "Project ID must be a ULID.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null) return SessionRequired();
        var active = await activeConversations.RecallAsync(
            profile.TenantId, profile.Id, projectId, cancellationToken);
        if (active is null) return Results.NoContent();

        // A conversa pode ter sido arquivada desde a última visita. Nunca restaure uma seleção
        // obsoleta: esqueça-a e deixe a UI escolher uma conversa ativa do projeto.
        var conversation = await conversations.GetConversationAsync(
            profile.TenantId, active.ConversationId, cancellationToken);
        if (conversation is null ||
            !string.Equals(conversation.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(conversation.State, "active", StringComparison.Ordinal))
        {
            await activeConversations.ForgetAsync(
                profile.TenantId, profile.Id, projectId, cancellationToken);
            return Results.NoContent();
        }

        return Results.Ok(new ActiveConversationResponse(active.ConversationId));
    }

    private static async Task<IResult> RememberActiveAsync(
        string projectId,
        RememberActiveConversationRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProfileActiveConversationStore activeConversations,
        IConversationStore conversations,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!UlidValue.TryParse(projectId, out _) ||
            input is null ||
            !UlidValue.TryParse(input.ConversationId, out _))
        {
            return Problem(
                400,
                "invalid_active_conversation",
                "Project and conversation IDs must be ULIDs.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null) return SessionRequired();
        var conversation = await conversations.GetConversationAsync(
            profile.TenantId, input.ConversationId, cancellationToken);
        if (conversation is null ||
            !string.Equals(conversation.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(conversation.State, "active", StringComparison.Ordinal))
        {
            return ConversationNotFound();
        }

        await activeConversations.RememberAsync(
            new ProfileActiveConversationRecord(
                profile.TenantId,
                profile.Id,
                projectId,
                conversation.Id,
                clock.UtcNow),
            cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RenameConversationAsync(
        string conversationId,
        RenameConversationRequest input,
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
        try
        {
            if (input is null || input.Title is null)
            {
                throw new ArgumentException("A conversation title is required.", nameof(input));
            }

            var now = clock.UtcNow;
            // Reaproveita a validação canônica de título do domínio (obrigatório, <=200).
            var renamed = new Conversation(
                current.Id, current.ProjectId, current.Title, current.State,
                current.CreatedByProfileId, current.CreatedAt, current.LastMessageAt, current.Version)
                .Rename(input.Title, now);
            var result = await store.RenameConversationAsync(
                profile.TenantId, conversationId, current.Version, renamed.Title, now, cancellationToken);
            return result.Status switch
            {
                ConversationMutationStatus.Applied => Results.Ok(ToResponse(result.Conversation!)),
                ConversationMutationStatus.NotFound => ConversationNotFound(),
                ConversationMutationStatus.VersionConflict => Problem(
                    409, "conversation_version_conflict", "The conversation changed concurrently."),
                _ => throw new InvalidOperationException(
                    $"Unexpected conversation rename status {result.Status}."),
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
        IProfileActiveConversationStore activeConversations,
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
        if (result.Status == ConversationMutationStatus.Applied)
        {
            var active = await activeConversations.RecallAsync(
                profile.TenantId, profile.Id, current.ProjectId, cancellationToken);
            if (string.Equals(active?.ConversationId, conversationId, StringComparison.Ordinal))
            {
                await activeConversations.ForgetAsync(
                    profile.TenantId, profile.Id, current.ProjectId, cancellationToken);
            }
            return Results.NoContent();
        }

        return result.Status switch
        {
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
        IWorkBoardStore board,
        ISolicitationAttachmentStore attachments,
        SolicitationAttachmentStorage attachmentStorage,
        IDocumentCatalogStore documents,
        IPrototypeStore prototypes,
        ChiefInvocationRoutingService routing,
        Readiness.ProjectReadinessService readiness,
        [FromServices] AgentAccountRegistry accounts,
        IChannelLinkStore channelLinks,
        IConfiguration configuration,
        [FromServices] V3BuildRuntimeService v3Runtime,
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
            var correlationId = $"turn:{turnId}";
            var userAt = now.AddMilliseconds(1);
            var user = ConversationApplicationService.CreateUserMessage(
                UlidValue.New(userAt).ToString(), profile.Id,
                new CreateMessageRequest(conversationId, input.Content), userAt);
            var userRecord = ToRecord(profile.TenantId, conversation.ProjectId, user);

            var v3Decision = await TryApplyV3NaturalDecisionAsync(
                input.Content,
                profile,
                project,
                conversationId,
                turnId,
                correlationId,
                userRecord,
                conversations,
                board,
                attachments,
                attachmentStorage,
                documents,
                prototypes,
                readiness,
                accounts,
                channelLinks,
                configuration,
                v3Runtime,
                clock,
                cancellationToken);
            if (v3Decision is not null)
            {
                return v3Decision;
            }

            var snapshot = await readiness.EvaluateAsync(
                profile.TenantId, project, profileReady: true, cancellationToken);

            // A execução só é tentada quando a prontidão canônica autoriza. Sem provider,
            // modelo ou workflow o turno não vira erro de requisição: a mensagem é persistida e
            // o bloqueio é devolvido tipado, sem fabricar resposta do Chief (ADR-019).
            ChiefInvocationSelection? selection = null;
            var blockers = new List<ChatTurnBlocker>();
            var executionStep = snapshot.Steps.Single(
                step => step.Step == ReadinessStep.ExecutionReady);
            if (executionStep.State is ConfigurationState.Ready or ConfigurationState.Simulated)
            {
                try
                {
                    selection = await routing.ResolveAsync(
                        profile.TenantId, project.ChiefAgentId, input, cancellationToken);
                }
                catch (ChiefInvocationSelectionException)
                {
                    // Corrida entre a leitura de prontidão e a resolução real: trate como
                    // bloqueio tipado, nunca como resposta simulada ou 400.
                    blockers.Add(new ChatTurnBlocker("chief.model_unresolved", []));
                }
            }
            else
            {
                blockers.AddRange(executionStep.Blockers
                    .Select(blocker => new ChatTurnBlocker(blocker.Code, blocker.RelatedIds)));
                if (blockers.Count == 0)
                {
                    blockers.Add(new ChatTurnBlocker("execution.not_ready", []));
                }
            }

            var links = new ChatTurnLinks(
                $"/api/v1/projects/{project.Id}/readiness",
                $"/api/v1/conversations/{conversationId}");
            var readinessContract = new ChatTurnReadiness(
                snapshot.OverallState.ToString(), executionStep.State.ToString());
            if (selection is null)
            {
                var nextActions = snapshot.NextActions
                    .Select(action => new ChatTurnNextAction(action.Code, action.Route, action.ResourceId))
                    .ToArray();
                var block = await chiefTurns.BlockAsync(
                    new ChiefTurnBlockCommand(
                        profile.TenantId,
                        project.Id,
                        conversationId,
                        turnId,
                        userRecord,
                        readinessContract.ExecutionState,
                        correlationId,
                        blockers.Select(blocker =>
                            new ChiefTurnBlockerRecord(blocker.Code, blocker.RelatedIds)).ToArray(),
                        nextActions.Select(action =>
                            new ChiefTurnNextActionRecord(action.Code, action.Route, action.ResourceId)).ToArray(),
                        $"chief-turn-block:{turnId}",
                        now),
                    cancellationToken);
                return Results.Accepted(value: new ChatTurnHandle(
                    block.TurnId,
                    conversationId,
                    "blocked",
                    block.CorrelationId,
                    readinessContract,
                    block.Blockers.Select(blocker =>
                        new ChatTurnBlocker(blocker.Code, blocker.RelatedIds)).ToArray(),
                    block.NextActions.Select(action =>
                        new ChatTurnNextAction(action.Code, action.Route, action.ResourceId)).ToArray(),
                    links));
            }

            await chiefTurns.EnqueueAsync(
                new ChiefTurnEnqueueCommand(
                    profile.TenantId,
                    project.Id,
                    conversationId,
                    turnId,
                    project.ChiefAgentId,
                    userRecord,
                    $"chief-turn:{turnId}",
                    now,
                    selection),
                cancellationToken);
            return Results.Accepted(value: new ChatTurnHandle(
                turnId, conversationId, "pending", correlationId, readinessContract, [], [], links));
        }
        catch (ArgumentException exception)
        {
            return Problem(400, "invalid_chat_turn", exception.Message);
        }
        catch (ChiefTurnConflictException exception)
        {
            return Problem(409, "chief_turn_conflict", exception.Message);
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

    private static async Task<IResult?> TryApplyV3NaturalDecisionAsync(
        string content,
        LocalProfileRecord profile,
        ProjectRecord project,
        string conversationId,
        string turnId,
        string correlationId,
        MessageRecord userRecord,
        IConversationStore conversations,
        IWorkBoardStore board,
        ISolicitationAttachmentStore attachments,
        SolicitationAttachmentStorage attachmentStorage,
        IDocumentCatalogStore documents,
        IPrototypeStore prototypes,
        Readiness.ProjectReadinessService readiness,
        AgentAccountRegistry accounts,
        IChannelLinkStore channelLinks,
        IConfiguration configuration,
        V3BuildRuntimeService runtime,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var deadline = V3NaturalUserDecision.ExtractDeadline(content);
        var authorized = V3AuthorizationPolicy.IsAuthorized(content);
        if (deadline is null && !authorized) return null;

        var understandStore = V3UnderstandStore.ForConfiguration(configuration);
        var context = await V3ProjectContextBuilder.BuildAsync(
            profile,
            project,
            board,
            attachments,
            documents,
            prototypes,
            attachmentStorage,
            readiness,
            accounts,
            channelLinks,
            understandStore,
            cancellationToken);
        var analyzed = V3UnderstandAnalyzer.Analyze(context, new V3UnderstandAnalyzeRequest
        {
            Deadline = deadline,
            Repository = context.Repository,
        }, clock.UtcNow);
        understandStore.WriteProject(analyzed.State);

        if (!authorized) return null;

        var refreshed = await V3ProjectContextBuilder.BuildAsync(
            profile,
            project,
            board,
            attachments,
            documents,
            prototypes,
            attachmentStorage,
            readiness,
            accounts,
            channelLinks,
            understandStore,
            cancellationToken);
        var ready = refreshed.Readiness.All(item => item.Status is "PASS" or "NOT_APPLICABLE");
        var sourceComplete = refreshed.PrimaryRequirementsCoverage.All(source => source.Complete);
        if (!ready || !sourceComplete || string.IsNullOrWhiteSpace(refreshed.Repository))
        {
            return null;
        }

        var now = clock.UtcNow;
        var state = (refreshed.State ?? V3ProjectUnderstandState.Create(project.Id, now)) with
        {
            Deadline = refreshed.Deadline,
            Repository = refreshed.Repository,
            AuthorizedAt = now,
            StartedAt = now,
            LifecycleState = "BUILDING",
            Status = "AUTHORIZED",
            UpdatedAt = now,
        };
        understandStore.WriteProject(state);
        var authorizedContext = await V3ProjectContextBuilder.BuildAsync(
            profile,
            project,
            board,
            attachments,
            documents,
            prototypes,
            attachmentStorage,
            readiness,
            accounts,
            channelLinks,
            understandStore,
            cancellationToken);
        var mission = V3MissionCompiler.CompileBuildMission(
            authorizedContext,
            V3ExecutorPreview.Select(accounts.List()),
            null,
            now);
        understandStore.WriteMission(mission);
        var persisted = await conversations.CreateMessageAsync(
            new MessageCreateCommand(profile.TenantId, userRecord, now),
            cancellationToken);
        if (persisted.Status is not MessageMutationStatus.Applied and not MessageMutationStatus.AlreadyExists)
        {
            return persisted.Status == MessageMutationStatus.ConversationInactive
                ? Problem(409, "conversation_inactive", "The conversation is not active.")
                : ConversationNotFound();
        }

        var dispatchEnabled = configuration.GetValue(
            "Harness:V3:NaturalAuthorizationAutoDispatchEnabled",
            true);
        if (dispatchEnabled)
        {
            var dispatchAccounts = accounts.List().ToArray();
            var dispatchState = state;
            _ = Task.Run(async () =>
            {
                await runtime.DispatchAsync(
                    new V3BuildDispatchCommand(project.Id, dispatchState, mission, "READY", dispatchAccounts),
                    CancellationToken.None);
            }, CancellationToken.None);
        }

        return Results.Accepted(value: new ChatTurnHandle(
            turnId,
            conversationId,
            dispatchEnabled ? "build_dispatched" : "build_compiled",
            correlationId,
            new ChatTurnReadiness("Ready", "Ready"),
            [],
            [],
            new ChatTurnLinks(
                $"/api/v1/projects/{project.Id}/readiness",
                $"/api/v1/conversations/{conversationId}")));
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

public sealed record RenameConversationRequest(string? Title);

public sealed record ConversationPage(IReadOnlyList<ConversationResponse> Items, string? NextCursor);
public sealed record MessagePage(IReadOnlyList<MessageResponse> Items, string? NextCursor);
