using Harness.Host.Profiles;
using Harness.Modules.Delivery.Application;
using Harness.Modules.Delivery.Contracts;
using Harness.Persistence.Abstractions.Delivery;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Delivery;

/// <summary>
/// Central de Entregas (Tech Lead) — endpoints READ-ONLY/aditivos. DEL-01 portfólio (+ visão "precisa
/// da minha atenção"), DEL-02 Projeto 360, DEL-09 previsão honesta (com histórico append-only). Uma
/// "entrega" é um projeto; o id da entrega é o projectId. Nada aqui muda o comportamento existente.
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
        return endpoints;
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
