using System.Text.Json;
using Harness.Modules.Conversations.Application;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Notifications;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.V3;

public sealed class V3DeliveryHandoffStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;

    public V3DeliveryHandoffStore(string dataDir)
    {
        _root = Path.Combine(dataDir, "v3", "handoffs");
        Directory.CreateDirectory(_root);
    }

    public static V3DeliveryHandoffStore ForConfiguration(IConfiguration configuration)
    {
        var dataDir = configuration["Harness:DataDir"];
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".harness-poseidon");
        }

        return new V3DeliveryHandoffStore(dataDir);
    }

    public V3DeliveryHandoffRecord? Read(string projectId, string validationExecutionId)
    {
        var path = PathFor(projectId, validationExecutionId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<V3DeliveryHandoffRecord>(File.ReadAllText(path), Json)
            : null;
    }

    public IReadOnlyList<V3DeliveryHandoffRecord> ListProject(string projectId) =>
        Directory.EnumerateFiles(_root, $"{projectId}-*.json", SearchOption.TopDirectoryOnly)
            .Select(path => JsonSerializer.Deserialize<V3DeliveryHandoffRecord>(File.ReadAllText(path), Json))
            .Where(value => value is not null)
            .Select(value => value!)
            .OrderBy(value => value.CreatedAt)
            .ToArray();

    public void Write(V3DeliveryHandoffRecord record)
    {
        File.WriteAllText(PathFor(record.ProjectId, record.ValidationExecutionId),
            JsonSerializer.Serialize(record, Json));
    }

    private string PathFor(string projectId, string validationExecutionId) =>
        Path.Combine(_root, $"{projectId}-{validationExecutionId}.json");
}

public sealed partial class V3DeliveryHandoffService(
    V3DeliveryHandoffStore store,
    V3BuildRuntimeStore runtimeStore,
    V3UnderstandStore understandStore,
    IProjectStore projects,
    ILocalProfileStore profiles,
    IConversationStore conversations,
    INotificationStore notifications,
    IClock clock,
    ILogger<V3DeliveryHandoffService> logger)
{
    public async Task<V3DeliveryHandoffRecord?> EnsureAsync(
        string tenantId,
        V3BuildExecutionRecord validationExecution,
        CancellationToken token)
    {
        if (!string.Equals(validationExecution.MissionType, "VALIDATE", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(validationExecution.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var project = await projects.GetAsync(tenantId, validationExecution.ProjectId, token);
        if (project is null)
        {
            LogHandoffSkipped(logger, validationExecution.ProjectId, "project_not_found");
            return null;
        }

        var state = understandStore.ReadProject(validationExecution.ProjectId);
        var access = ProductAccessInfo.From(
            validationExecution,
            runtimeStore,
            validationExecution.ProjectId,
            state?.Repository);
        var existing = store.Read(validationExecution.ProjectId, validationExecution.MissionExecutionId);
        var content = V3DeliveryHandoffMessage.Compose(
            project.Name,
            access,
            validationExecution.ValidationReport,
            validationExecution.FinalReport);

        if (existing is not null)
        {
            var refreshed = await RefreshHandoffAsync(
                tenantId, project, validationExecution, existing, access, content, token);
            return refreshed;
        }

        return await CreateHandoffAsync(
            tenantId, project, validationExecution, access, content, token);
    }

    private async Task<V3DeliveryHandoffRecord?> RefreshHandoffAsync(
        string tenantId,
        ProjectRecord project,
        V3BuildExecutionRecord validationExecution,
        V3DeliveryHandoffRecord existing,
        ProductAccessInfo access,
        string content,
        CancellationToken token)
    {
        var now = clock.UtcNow;
        var refreshed = existing with { ProductAccess = access };

        var previousMessage = await conversations.GetMessageAsync(tenantId, existing.ConversationMessageId, token);
        if (previousMessage is not null &&
            string.Equals(previousMessage.Content, content, StringComparison.Ordinal))
        {
            store.Write(refreshed);
            return refreshed;
        }

        var conversation = await conversations.GetConversationAsync(tenantId, existing.ConversationId, token);
        if (conversation is null)
        {
            store.Write(refreshed);
            return refreshed;
        }

        var messageId = UlidValue.New(now).ToString();
        var message = ConversationApplicationService.CreateChiefMessage(
            messageId,
            conversation.Id,
            project.ChiefAgentId,
            content,
            now);
        var persisted = await conversations.CreateMessageAsync(
            new MessageCreateCommand(tenantId, ToRecord(tenantId, validationExecution.ProjectId, message), now),
            token);
        if (persisted.Status is MessageMutationStatus.Applied or MessageMutationStatus.AlreadyExists)
        {
            refreshed = refreshed with { ConversationMessageId = persisted.Message?.Id ?? messageId };
        }

        store.Write(refreshed);
        return refreshed;
    }

    private async Task<V3DeliveryHandoffRecord?> CreateHandoffAsync(
        string tenantId,
        ProjectRecord project,
        V3BuildExecutionRecord validationExecution,
        ProductAccessInfo access,
        string content,
        CancellationToken token)
    {
        var conversation = (await conversations.ListConversationsAsync(
                tenantId, validationExecution.ProjectId, null, 200, token))
            .Where(item => string.Equals(item.State, "active", StringComparison.Ordinal))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        var owner = conversation is null
            ? (await profiles.ListAsync(token)).FirstOrDefault(profile => profile.TenantId == tenantId)
            : await profiles.GetAsync(conversation.CreatedByProfileId, token);
        if (owner is null)
        {
            LogHandoffSkipped(logger, validationExecution.ProjectId, "profile_not_found");
            return null;
        }

        var now = clock.UtcNow;
        conversation ??= await CreatePrimaryConversationAsync(
            tenantId, validationExecution.ProjectId, owner.Id, now, token);

        var messageId = UlidValue.New(now).ToString();
        var notificationId = UlidValue.New(now.AddTicks(1)).ToString();

        var message = ConversationApplicationService.CreateChiefMessage(
            messageId,
            conversation.Id,
            project.ChiefAgentId,
            content,
            now);
        var persisted = await conversations.CreateMessageAsync(
            new MessageCreateCommand(tenantId, ToRecord(tenantId, validationExecution.ProjectId, message), now),
            token);
        if (persisted.Status is not MessageMutationStatus.Applied and not MessageMutationStatus.AlreadyExists)
        {
            LogHandoffSkipped(logger, validationExecution.ProjectId, $"message_{persisted.Status}");
            return null;
        }

        var notification = await notifications.CreateAsync(
            new NotificationCreateCommand(
                tenantId,
                notificationId,
                owner.Id,
                "info",
                "chat",
                $"{project.Name} está pronto para homologação",
                "Abra a Central de Entregas no Poseidon para acessar URLs, evidências e instruções de homologação. As senhas TEST_ONLY ficam em 'Mostrar credenciais'; não são enviadas por notificação.",
                $"v3-handoff:{validationExecution.ProjectId}:{validationExecution.MissionExecutionId}",
                $"/delivery?project={validationExecution.ProjectId}",
                now),
            token);

        var record = new V3DeliveryHandoffRecord(
            validationExecution.ProjectId,
            validationExecution.MissionExecutionId,
            validationExecution.MissionId,
            validationExecution.CurrentHead,
            conversation.Id,
            persisted.Message?.Id ?? messageId,
            notification.Id,
            access,
            now);
        store.Write(record);
        return record;
    }

    private async Task<ConversationRecord> CreatePrimaryConversationAsync(
        string tenantId,
        string projectId,
        string profileId,
        DateTimeOffset now,
        CancellationToken token)
    {
        var contract = ConversationApplicationService.Create(
            UlidValue.New(now.AddTicks(2)).ToString(),
            profileId,
            new Harness.Modules.Conversations.Contracts.CreateConversationRequest(projectId, "Conversa inicial"),
            now);
        var result = await conversations.CreateConversationAsync(
            new ConversationCreateCommand(
                new ConversationRecord(
                    tenantId,
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
        if (result.Conversation is null)
        {
            throw new InvalidOperationException($"Unable to create delivery handoff conversation: {result.Status}.");
        }

        return result.Conversation;
    }

    private static MessageRecord ToRecord(string tenantId, string projectId, Harness.Modules.Conversations.Contracts.MessageContract value) =>
        new(
            tenantId,
            projectId,
            value.Id,
            value.ConversationId,
            value.AuthorRole,
            value.AuthorProfileId,
            value.AuthorAgentId,
            value.Content,
            value.TokenCount,
            value.CreatedAt);

    [LoggerMessage(EventId = 31001, Level = LogLevel.Warning, Message = "V3 delivery handoff skipped for project {ProjectId}: {Reason}.")]
    private static partial void LogHandoffSkipped(ILogger logger, string projectId, string reason);
}

public sealed record V3DeliveryHandoffRecord(
    string ProjectId,
    string ValidationExecutionId,
    string ValidationMissionId,
    string? DeliveredHead,
    string ConversationId,
    string ConversationMessageId,
    string NotificationId,
    ProductAccessInfo ProductAccess,
    DateTimeOffset CreatedAt);

public sealed record ProductAccessInfo(
    string? ApplicationUrl,
    string? ApiUrl,
    string? SwaggerUrl,
    string? HealthUrl,
    string? StartCommand,
    string? StopCommand,
    string? StatusCommand,
    IReadOnlyList<TestAccountInfo> TestAccounts,
    string RuntimeStatus,
    DateTimeOffset? LastVerifiedAt)
{
    public static ProductAccessInfo From(
        V3BuildExecutionRecord validationExecution,
        V3BuildRuntimeStore runtimeStore,
        string projectId,
        string? repository)
    {
        var readme = ReadRepositoryReadme(repository);
        var text = string.Join('\n', validationExecution.FinalReport, validationExecution.LastOutput, readme);
        var manifestAccess = TryExtractFromManifest(text);
        var latestBuild = runtimeStore.ListExecutions()
            .Where(item => item.ProjectId == projectId &&
                           string.Equals(item.MissionType, "BUILD", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.StartedAt)
            .FirstOrDefault();
        _ = latestBuild;
        var app = manifestAccess.ApplicationUrl ?? FirstUrl(text, "ApplicationUrl", "Application", "App", "Aplicação");
        var apiUrl = manifestAccess.ApiUrl;
        var swagger = manifestAccess.SwaggerUrl ?? FirstUrl(text, "Swagger");
        var health = manifestAccess.HealthUrl ?? FirstUrl(text, "HealthUrl", "Health", "Readiness");
        var startCommand = FirstCommand(text, "StartCommand", "Iniciar", "Execução") ?? RelativeScript(repository, "scripts/run.sh");
        var accessCommand = FirstCommand(text, "Acesso TEST_ONLY", "Acesso", "Access", "Credenciais");
        var accessScriptText = ReadAccessScript(repository, accessCommand);
        var accounts = manifestAccess.TestAccounts.Count > 0
            ? manifestAccess.TestAccounts
            : ParseTestAccounts(string.Join('\n', text, accessScriptText));
        var effectiveHealth = health ?? apiUrl;
        return new ProductAccessInfo(
            app,
            apiUrl,
            swagger,
            health,
            startCommand,
            FirstCommand(text, "StopCommand", "Parar"),
            FirstCommand(text, "StatusCommand", "Status"),
            accounts,
            effectiveHealth is null ? "UNKNOWN" : "REPORTED_HEALTHY",
            validationExecution.CompletedAt);
    }

    private static (string? ApplicationUrl, string? ApiUrl, string? SwaggerUrl, string? HealthUrl, IReadOnlyList<TestAccountInfo> TestAccounts)
        TryExtractFromManifest(string text)
    {
        if (!V3ValidationEvidenceGate.TryExtractManifest(text, null, out var manifest, out _) || manifest.HandoffReadiness is null)
        {
            return (null, null, null, null, []);
        }

        var readiness = manifest.HandoffReadiness;
        var app = readiness.ApplicationUrl;
        var apiUrl = manifest.BrowserRuns.FirstOrDefault(run => !string.IsNullOrWhiteSpace(run.ApiUrl))?.ApiUrl;
        var swagger = apiUrl is not null ? $"{apiUrl.TrimEnd('/')}/swagger" : null;
        var health = apiUrl is not null ? $"{apiUrl.TrimEnd('/')}/health" : null;
        var accounts = ParseTestAccountsFromCredentialString(readiness.TestCredentialsCapturedWhenApplicable);
        return (app, apiUrl, swagger, health, accounts);
    }

    private static readonly char[] CredentialRoleSeparators = ['/', ',', ';'];

    private static List<TestAccountInfo> ParseTestAccountsFromCredentialString(string? credentialText)
    {
        if (string.IsNullOrWhiteSpace(credentialText)) return [];
        var accounts = new List<TestAccountInfo>();
        var passwordMatch = System.Text.RegularExpressions.Regex.Match(
            credentialText,
            @"(?im)senha\s*[:=]?\s*([^\s,;()]+)");
        var password = passwordMatch.Success ? passwordMatch.Groups[1].Value.Trim() : null;
        var labelMatch = System.Text.RegularExpressions.Regex.Match(
            credentialText,
            @"(?im)(?:TEST_ONLY\s*[:=]?\s*)([^,;()]+)");
        if (!labelMatch.Success) return accounts;
        var rolesPart = labelMatch.Groups[1].Value.Trim();
        foreach (var role in rolesPart.Split(CredentialRoleSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = role.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Equals("senha", StringComparison.OrdinalIgnoreCase)) continue;
            accounts.Add(new TestAccountInfo(
                trimmed,
                trimmed,
                password,
                "TEST_ONLY",
                "Credencial local de homologação registrada pelo produto; não enviar por canais externos."));
        }

        return accounts;
    }

    private static string? ReadRepositoryReadme(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository) || !Directory.Exists(repository))
        {
            return null;
        }

        var path = Path.Combine(repository, "README.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string? ReadAccessScript(string? repository, string? relativeCommand)
    {
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(relativeCommand))
        {
            return null;
        }

        var path = Path.Combine(repository, relativeCommand.TrimStart('.', '/'));
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string? FirstUrl(string text, params string[] labels)
    {
        foreach (var label in labels)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                text,
                $@"(?im)^\s*[-*]?\s*{System.Text.RegularExpressions.Regex.Escape(label)}(?:\s*\([^)]*\))?\s*:?\s*(https?://\S+)");
            if (match.Success) return match.Groups[1].Value.TrimEnd('.', ',', ';');
        }

        return System.Text.RegularExpressions.Regex.Match(text, @"https?://\S+") is { Success: true } any
            ? any.Value.TrimEnd('.', ',', ';')
            : null;
    }

    private static string? FirstCommand(string text, params string[] labels)
    {
        foreach (var label in labels)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                text,
                $@"(?im)^\s*[-*]?\s*{System.Text.RegularExpressions.Regex.Escape(label)}\s*:?\s*`([^`]+)`");
            if (match.Success) return match.Groups[1].Value.Trim();
        }

        return null;
    }

    private static string? RelativeScript(string? repository, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        var path = Path.Combine(repository, relativePath);
        return File.Exists(path) ? $"./{relativePath}" : null;
    }

    private static List<TestAccountInfo> ParseTestAccounts(string text)
    {
        var accounts = new List<TestAccountInfo>();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     text,
                     @"(?im)^\s*\|?\s*(?<role>[^|\r\n]+?)\s*\|\s*`?(?<user>[^`|\s]+@[^`|\s]+)`?\s*\|\s*`?(?<password>[^`|\r\n]+)`?"))
        {
            var role = match.Groups["role"].Value.Trim();
            if (role.Equals("---", StringComparison.Ordinal) ||
                role.Contains("perfil", StringComparison.OrdinalIgnoreCase) ||
                role.Contains("Perfil", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            accounts.Add(new TestAccountInfo(
                role,
                match.Groups["user"].Value.Trim(),
                match.Groups["password"].Value.Trim(),
                "TEST_ONLY",
                "Credencial local de homologação registrada pelo produto; não enviar por canais externos."));
        }

        return accounts;
    }
}

public sealed record TestAccountInfo(string Role, string Username, string? Password, string Classification, string? Notes);

public static class V3DeliveryHandoffMessage
{
    public static string Compose(
        string projectName,
        ProductAccessInfo access,
        V3ValidationReport? report,
        string? finalReport)
    {
        var lines = new List<string>
        {
            $"Concluímos o desenvolvimento e a validação de {projectName}.",
            "",
            "O produto está pronto para sua homologação.",
        };
        if (!string.IsNullOrWhiteSpace(access.ApplicationUrl))
        {
            lines.Add("");
            lines.Add($"Aplicação: {access.ApplicationUrl}");
        }
        if (!string.IsNullOrWhiteSpace(access.SwaggerUrl))
        {
            lines.Add($"Swagger: {access.SwaggerUrl}");
        }
        if (!string.IsNullOrWhiteSpace(access.HealthUrl))
        {
            lines.Add($"Health: {access.HealthUrl}");
        }
        if (access.TestAccounts.Count > 0)
        {
            lines.Add("");
            lines.Add("Acesso de homologação:");
            foreach (var account in access.TestAccounts)
            {
                lines.Add($"- {account.Role}: {account.Username}");
            }

            lines.Add("");
            lines.Add("A senha de homologação não é enviada pelo chat ou por notificações.");
            lines.Add("Abra a Central de Entregas no Poseidon e clique em 'Mostrar credenciais' para visualizar/copiar as credenciais TEST_ONLY.");
        }
        if (report is not null)
        {
            lines.Add("");
            lines.Add("Validação:");
            lines.Add($"- requisitos: {report.RequirementsPassed}/{report.RequirementsChecked}");
            lines.Add($"- checklist: {report.ChecklistPass} PASS, {report.ChecklistFixed} FIXED, {report.ChecklistNA} N/A, {report.ChecklistFail} FAIL");
            lines.Add($"- testes de navegador: {report.BrowserTestsPassed} pass, {report.BrowserTestsFailed} fail, {report.BrowserTestsSkipped} skip");
            lines.Add($"- bugs encontrados/corrigidos: {report.BugsFound}/{report.BugsFixed}");
        }
        lines.Add("");
        lines.Add("O produto está no estado Aceite Humano. Eu não marquei a entrega como aceita por você.");
        lines.Add("Quando terminar sua homologação, você pode aprovar ou reportar um problema.");
        if (string.IsNullOrWhiteSpace(access.ApplicationUrl) && string.IsNullOrWhiteSpace(finalReport))
        {
            lines.Add("");
            lines.Add("Observação: as instruções de acesso ainda não foram estruturadas pelo executor; consulte a Central de Entregas para detalhes técnicos registrados.");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
