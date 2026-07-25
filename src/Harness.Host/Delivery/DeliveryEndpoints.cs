using Harness.Host.Profiles;
using Harness.Modules.Delivery.Application;
using Harness.Modules.Delivery.Contracts;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Delivery;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Delivery;

/// <summary>
/// Central de Entregas — projeções rastreáveis e uma única mutação de planejamento explicitamente
/// humana. DEL-01 portfólio (+ visão "precisa da minha atenção"), DEL-02 Projeto 360, DEL-09
/// previsão honesta (com histórico append-only). Uma "entrega" é um projeto; o id é o projectId.
/// </summary>
public static class DeliveryEndpoints
{
    public static IEndpointRouteBuilder MapDeliveries(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/deliveries").WithTags("deliveries");
        group.MapGet("/", ListDeliveriesAsync)
            .Produces<DeliveryPortfolioContract>().ProducesProblem(400).ProducesProblem(401);
        group.MapGet("/{deliveryId}/overview", GetOverviewAsync)
            .Produces<Delivery360Contract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapGet("/{deliveryId}/forecast", GetForecastHistoryAsync)
            .Produces<DeliveryForecastHistoryContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapPost("/{deliveryId}/forecast", AppendForecastAsync)
            .Produces<DeliveryForecastContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapPost("/{deliveryId}/planning", ConfigurePlanningAsync)
            .Produces<DeliveryPlanningContract>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(404).ProducesProblem(409);

        // DEL-04/DEL-05/DEL-10 — Central de Relatórios (documentos versionados, aprovação humana, envio).
        group.MapPost("/{deliveryId}/reports", GenerateReportAsync)
            .Produces<DeliveryReportContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapGet("/{deliveryId}/reports", ListReportsAsync)
            .Produces<DeliveryReportListContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapGet("/{deliveryId}/reports/{reportId}", GetReportAsync)
            .Produces<DeliveryReportContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapPost("/{deliveryId}/reports/{reportId}/approve", ApproveReportAsync)
            .Produces<DeliveryReportContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("/{deliveryId}/reports/{reportId}/send", SendReportAsync)
            .Produces<DeliveryReportSendReceiptContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);

        // DEL-03 — Daily Copilot (briefing pré-daily, captura de marcações tipadas, resumo pós-daily).
        group.MapGet("/{deliveryId}/daily/briefing", GetDailyBriefingAsync)
            .Produces<DailyBriefingContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapPost("/{deliveryId}/daily/captures", CaptureDailyAsync)
            .Produces<DailyCaptureContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapGet("/{deliveryId}/daily/summary", GetDailySummaryAsync)
            .Produces<DailySummaryContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);

        // DEL-06 — Métricas DORA + próprias, derivadas estritamente de dados gravados.
        group.MapGet("/{deliveryId}/metrics", GetMetricsAsync)
            .Produces<DeliveryMetricsContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> GetDailyBriefingAsync(
        string deliveryId, HttpRequest request, ILocalProfileStore profiles,
        DeliveryReadModelService service, IDeliveryDailyStore daily, CancellationToken token)
    {
        if (!Valid(deliveryId)) return InvalidId("delivery");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();

        var input = await service.BuildForProjectAsync(profile.TenantId, deliveryId, token);
        if (input is null) return NotFound("delivery");

        var captures = await LoadCapturesAsync(daily, profile.TenantId, deliveryId, token);
        return Results.Ok(DailyCopilotComposer.Briefing(input, captures));
    }

    private static async Task<IResult> CaptureDailyAsync(
        string deliveryId, DailyCaptureRequest? body, HttpRequest request, ILocalProfileStore profiles,
        DeliveryReadModelService service, IDeliveryDailyStore daily, IClock clock, CancellationToken token)
    {
        if (!Valid(deliveryId)) return InvalidId("delivery");
        if (body is null) return Invalid("body", "A request body is required.");
        var kind = body.Kind?.Trim();
        if (!DailyCaptureKinds.IsValid(kind))
        {
            return Invalid("kind",
                $"kind must be one of: {string.Join(", ", DailyCaptureKinds.All)}.");
        }

        if (string.IsNullOrWhiteSpace(body.Note))
        {
            return Invalid("note", "A note is required.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();

        // A entrega precisa existir; NÃO tocamos em cards de PO — só persistimos a nota durável.
        if (await service.BuildForProjectAsync(profile.TenantId, deliveryId, token) is null)
        {
            return NotFound("delivery");
        }

        var capturedBy = string.IsNullOrWhiteSpace(body.CapturedBy)
            ? profile.DisplayName ?? profile.Id
            : body.CapturedBy!.Trim();
        var now = clock.UtcNow;
        var record = await daily.AppendAsync(new DeliveryDailyCaptureAppendCommand(
            profile.TenantId, UlidValue.New(now).ToString(), deliveryId, kind!, body.Note.Trim(),
            capturedBy, now), token);

        return Results.Created(
            $"/api/v1/deliveries/{deliveryId}/daily/summary",
            new DailyCaptureContract(
                record.Id, record.ProjectId, record.Kind, record.Note, record.CapturedBy,
                record.CreatedAt));
    }

    private static async Task<IResult> GetDailySummaryAsync(
        string deliveryId, HttpRequest request, ILocalProfileStore profiles,
        DeliveryReadModelService service, IDeliveryDailyStore daily, CancellationToken token)
    {
        if (!Valid(deliveryId)) return InvalidId("delivery");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();

        var input = await service.BuildForProjectAsync(profile.TenantId, deliveryId, token);
        if (input is null) return NotFound("delivery");

        var captures = await LoadCapturesAsync(daily, profile.TenantId, deliveryId, token);
        return Results.Ok(DailyCopilotComposer.Summary(input, captures));
    }

    private static async Task<IResult> GetMetricsAsync(
        string deliveryId, HttpRequest request, ILocalProfileStore profiles,
        DeliveryReadModelService service, CancellationToken token)
    {
        if (!Valid(deliveryId)) return InvalidId("delivery");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();

        var input = await service.BuildForProjectAsync(profile.TenantId, deliveryId, token);
        if (input is null) return NotFound("delivery");
        return Results.Ok(DeliveryMetricsCalculator.Compute(input));
    }

    private static async Task<IReadOnlyList<DeliveryDailyCaptureFacts>> LoadCapturesAsync(
        IDeliveryDailyStore daily, string tenantId, string deliveryId, CancellationToken token)
    {
        var records = await daily.ListByProjectAsync(tenantId, deliveryId, 500, token);
        return records
            .Select(c => new DeliveryDailyCaptureFacts(
                c.Id, c.Kind, c.Note, c.CapturedBy, c.CreatedAt))
            .ToArray();
    }

    private static async Task<IResult> ListDeliveriesAsync(
        string? view, HttpRequest request, ILocalProfileStore profiles,
        DeliveryReadModelService service, CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();

        var selectedView = string.IsNullOrWhiteSpace(view) ? "portfolio" : view.Trim();
        if (selectedView is not ("portfolio" or "attention"))
        {
            return Invalid("view", "view must be 'portfolio' or 'attention'.");
        }

        var inputs = await service.BuildPortfolioAsync(profile.TenantId, token);
        var summaries = inputs
            .Select(DeliveryPortfolioProjector.Summarize)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.DeliveryId, StringComparer.Ordinal)
            .ToArray();

        var filtered = selectedView == "attention"
            ? summaries.Where(DeliveryPortfolioProjector.NeedsAttention).ToArray()
            : summaries;

        return Results.Ok(new DeliveryPortfolioContract(selectedView, filtered.Length, filtered));
    }

    private static async Task<IResult> GetOverviewAsync(
        string deliveryId, HttpRequest request, ILocalProfileStore profiles,
        DeliveryReadModelService service, CancellationToken token)
    {
        if (!Valid(deliveryId)) return InvalidId("delivery");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();

        var input = await service.BuildForProjectAsync(profile.TenantId, deliveryId, token);
        if (input is null) return NotFound("delivery");
        return Results.Ok(Delivery360Projector.Project(input));
    }

    private static async Task<IResult> GetForecastHistoryAsync(
        string deliveryId, HttpRequest request, ILocalProfileStore profiles,
        IDeliveryForecastStore forecasts, IProjectStore projects, CancellationToken token)
    {
        if (!Valid(deliveryId)) return InvalidId("delivery");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (await projects.GetAsync(profile.TenantId, deliveryId, token) is null) return NotFound("delivery");

        var history = await forecasts.ListByProjectAsync(profile.TenantId, deliveryId, 200, token);
        var contracts = history.Select(ToContract).ToArray();
        return Results.Ok(new DeliveryForecastHistoryContract(
            deliveryId, contracts.Length > 0 ? contracts[0] : null, contracts));
    }

    private static async Task<IResult> AppendForecastAsync(
        string deliveryId, HttpRequest request, ILocalProfileStore profiles,
        DeliveryReadModelService service, IDeliveryForecastStore forecasts, IClock clock,
        CancellationToken token)
    {
        if (!Valid(deliveryId)) return InvalidId("delivery");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();

        var input = await service.BuildForProjectAsync(profile.TenantId, deliveryId, token);
        if (input is null) return NotFound("delivery");

        // Recalcula a previsão honesta a partir do estado atual e ANEXA uma nova linha ao histórico —
        // nunca sobrescreve a anterior. Cada POST é uma foto auditável do forecast naquele instante.
        var aggregate = DeliveryFactsCalculator.Compute(input);
        var forecast = aggregate.Forecast;
        var now = clock.UtcNow;
        var record = await forecasts.AppendAsync(new DeliveryForecastAppendCommand(
            profile.TenantId, UlidValue.New(now).ToString(), deliveryId, forecast.ForecastDate,
            DeliveryForecaster.ToConfidenceString(forecast.Confidence), forecast.ConfidencePercent,
            forecast.HasSufficientEvidence,
            forecast.Basis.Select(b => new DeliveryForecastBasisEntry(b.Signal, b.Detail)).ToArray(),
            now), token);

        return Results.Created($"/api/v1/deliveries/{deliveryId}/forecast", ToContract(record));
    }

    private static async Task<IResult> ConfigurePlanningAsync(
        string deliveryId, DeliveryPlanningRequest? body, HttpRequest request,
        ILocalProfileStore profiles, IProjectStore projects, IAgentCatalogStore agents,
        IWorkBoardStore board, IDeliveryForecastStore forecasts, IClock clock,
        CancellationToken token)
    {
        if (!Valid(deliveryId)) return InvalidId("delivery");
        if (body is null) return Invalid("body", "A request body is required.");
        if (!Valid(body.OwnerAgentId)) return Invalid("ownerAgentId", "Owner agent ID must be a ULID.");
        if (body.CommittedDate.Offset != TimeSpan.Zero)
            return Invalid("committedDate", "Committed date must be UTC.");
        if (body.ForecastDate is { } requestedForecast &&
            requestedForecast.Offset != TimeSpan.Zero)
            return Invalid("forecastDate", "Forecast date must be UTC.");

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (await projects.GetAsync(profile.TenantId, deliveryId, token) is null)
            return NotFound("delivery");
        var agent = await agents.GetAgentAsync(profile.TenantId, body.OwnerAgentId, token);
        if (agent is null || agent.ProjectId != deliveryId)
            return Invalid("ownerAgentId", "The selected agent is not assigned to this project.");

        var active = new List<BoardTaskRecord>();
        string? afterId = null;
        for (var page = 0; page < 25; page++)
        {
            var batch = await board.ListTasksAsync(profile.TenantId, deliveryId, null, afterId, 200, token);
            active.AddRange(batch.Where(task => task.ArchivedAt is null && task.State != "done"));
            if (batch.Count < 200) break;
            afterId = batch[^1].Id;
        }
        if (active.Count == 0)
            return Problem(409, "delivery_has_no_active_tasks",
                "The delivery needs at least one active task before owner and date can be configured.");

        var now = clock.UtcNow;
        foreach (var task in active)
        {
            await board.SetTaskPlanningAsync(new(
                profile.TenantId, task.Id, agent.Id, body.CommittedDate, now), token);
        }

        if (body.ForecastDate is { } forecastDate)
        {
            await forecasts.AppendAsync(new(
                profile.TenantId,
                UlidValue.New(now.AddTicks(1)).ToString(),
                deliveryId,
                forecastDate,
                "low",
                25,
                true,
                [new(
                    "manual_forecast",
                    "Forecast date configured manually by the local profile.")],
                now), token);
        }

        return Results.Ok(new DeliveryPlanningContract(
            deliveryId, agent.Id, agent.Name, body.CommittedDate, body.ForecastDate,
            active.Count, now));
    }

    private static async Task<IResult> GenerateReportAsync(
        string deliveryId, GenerateReportRequest? body, HttpRequest request, ILocalProfileStore profiles,
        DeliveryReportService service, CancellationToken token)
    {
        if (!Valid(deliveryId)) return InvalidId("delivery");
        if (body is null) return Invalid("body", "A request body is required.");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();

        var result = await service.GenerateAsync(
            profile.TenantId, deliveryId, body.Type ?? string.Empty, body.Format ?? string.Empty,
            body.Audience, body.Classification, token);
        return result.IsError
            ? FromError(result.Error!)
            : Results.Created($"/api/v1/deliveries/{deliveryId}/reports/{result.Value!.Id}", result.Value);
    }

    private static async Task<IResult> ListReportsAsync(
        string deliveryId, HttpRequest request, ILocalProfileStore profiles,
        DeliveryReportService service, CancellationToken token)
    {
        if (!Valid(deliveryId)) return InvalidId("delivery");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();

        var result = await service.ListAsync(profile.TenantId, deliveryId, token);
        return result.IsError ? FromError(result.Error!) : Results.Ok(result.Value);
    }

    private static async Task<IResult> GetReportAsync(
        string deliveryId, string reportId, HttpRequest request, ILocalProfileStore profiles,
        DeliveryReportService service, CancellationToken token)
    {
        if (!Valid(deliveryId)) return InvalidId("delivery");
        if (!Valid(reportId)) return InvalidId("report");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();

        var result = await service.GetAsync(profile.TenantId, deliveryId, reportId, token);
        return result.IsError ? FromError(result.Error!) : Results.Ok(result.Value);
    }

    private static async Task<IResult> ApproveReportAsync(
        string deliveryId, string reportId, ApproveReportRequest? body, HttpRequest request,
        ILocalProfileStore profiles, DeliveryReportService service, CancellationToken token)
    {
        if (!Valid(deliveryId)) return InvalidId("delivery");
        if (!Valid(reportId)) return InvalidId("report");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();

        // O aprovador é o humano em sessão (identidade estável); um rótulo opcional pode acompanhá-lo.
        var approvedBy = string.IsNullOrWhiteSpace(body?.ApprovedBy)
            ? profile.DisplayName ?? profile.Id
            : body!.ApprovedBy!.Trim();
        var result = await service.ApproveAsync(profile.TenantId, deliveryId, reportId, approvedBy, token);
        return result.IsError ? FromError(result.Error!) : Results.Ok(result.Value);
    }

    private static async Task<IResult> SendReportAsync(
        string deliveryId, string reportId, SendReportRequest? body, HttpRequest request,
        ILocalProfileStore profiles, DeliveryReportService service, CancellationToken token)
    {
        if (!Valid(deliveryId)) return InvalidId("delivery");
        if (!Valid(reportId)) return InvalidId("report");
        if (body is null) return Invalid("body", "A request body is required.");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();

        var sentBy = profile.DisplayName ?? profile.Id;
        var result = await service.SendAsync(
            profile.TenantId, deliveryId, reportId, body.Channel ?? string.Empty,
            body.RecipientReference ?? string.Empty, sentBy, token);
        return result.IsError ? FromError(result.Error!) : Results.Ok(result.Value);
    }

    private static IResult FromError(ReportError error) =>
        Results.Problem(statusCode: error.Status, title: error.Title, detail: error.Detail);

    private static DeliveryForecastContract ToContract(DeliveryForecastRecord record) => new(
        record.Id, record.ForecastDate, record.Confidence, record.ConfidencePercent,
        record.HasSufficientEvidence,
        record.Basis.Select(b => new ForecastBasisContract(b.Signal, b.Detail)).ToArray(),
        record.CreatedAt);

    private static bool Valid(string id) => UlidValue.TryParse(id, out _);
    private static IResult SessionRequired() => Problem(401, "local_session_required", "A local profile session is required.");
    private static IResult InvalidId(string resource) => Problem(400, $"invalid_{resource}_id", "ID must be a ULID.");
    private static IResult Invalid(string resource, string detail) => Problem(400, $"invalid_{resource}", detail);
    private static IResult NotFound(string resource) => Problem(404, $"{resource}_not_found", "The resource does not exist.");
    private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
}

/// <summary>Pedido para gerar um relatório de {tipo, formato} — vira um snapshot em rascunho (DEL-04).</summary>
public sealed record GenerateReportRequest(string? Type, string? Format, string? Audience, string? Classification);

/// <summary>Aprovação humana de um relatório (DEL-04). O aprovador padrão é o humano em sessão.</summary>
public sealed record ApproveReportRequest(string? ApprovedBy);

/// <summary>
/// Envio externo de um relatório aprovado (DEL-10). O destinatário é uma REFERÊNCIA OPACA
/// (`env://`, `secret://`, `keychain://`) — nunca um endereço literal.
/// </summary>
public sealed record SendReportRequest(string Channel, string RecipientReference);

/// <summary>
/// Configura a origem persistida do responsável e da data comprometida: todas as tarefas ativas
/// passam a compartilhar o planejamento informado; a projeção da entrega é recalculada em realtime.
/// </summary>
public sealed record DeliveryPlanningRequest(
    string OwnerAgentId, DateTimeOffset CommittedDate, DateTimeOffset? ForecastDate = null);

public sealed record DeliveryPlanningContract(
    string DeliveryId, string OwnerAgentId, string OwnerName, DateTimeOffset CommittedDate,
    DateTimeOffset? ForecastDate, int UpdatedTaskCount, DateTimeOffset UpdatedAt);
