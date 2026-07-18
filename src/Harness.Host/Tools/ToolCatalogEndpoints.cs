using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Tools;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Tools;

public static class ToolCatalogEndpoints
{
    public static IEndpointRouteBuilder MapToolCatalog(this IEndpointRouteBuilder endpoints)
    {
        var skills = endpoints.MapGroup("/api/v1/skills").WithTags("tools");
        skills.MapGet("/", ListSkillsAsync).Produces<SkillPage>().ProducesProblem(400).ProducesProblem(401);
        skills.MapGet("/{id}", GetSkillAsync).Produces<SkillContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        skills.MapPatch("/{id}", PatchSkillAsync).Produces<SkillContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        var tools = endpoints.MapGroup("/api/v1/tools").WithTags("tools");
        tools.MapGet("/", ListToolsAsync).Produces<ToolPage>().ProducesProblem(400).ProducesProblem(401);
        tools.MapGet("/{id}", GetToolAsync).Produces<ToolContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        tools.MapPatch("/{id}", PatchToolAsync).Produces<ToolContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        var plugins = endpoints.MapGroup("/api/v1/plugins").WithTags("tools");
        plugins.MapGet("/", ListPluginsAsync).Produces<PluginPage>().ProducesProblem(400).ProducesProblem(401);
        plugins.MapGet("/{id}", GetPluginAsync).Produces<PluginContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        plugins.MapPatch("/{id}", PatchPluginAsync).Produces<PluginContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        var mcp = endpoints.MapGroup("/api/v1/mcp-servers").WithTags("tools");
        mcp.MapGet("/", ListMcpServersAsync).Produces<McpServerPage>().ProducesProblem(400).ProducesProblem(401);
        mcp.MapGet("/{id}", GetMcpServerAsync).Produces<McpServerContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        mcp.MapPatch("/{id}", PatchMcpServerAsync).Produces<McpServerContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        return endpoints;
    }

    private static Task<IResult> ListSkillsAsync(string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IToolCatalogStore store, CancellationToken token) => ListAsync("skills", cursor, limit, request, profiles, store, token);
    private static Task<IResult> ListToolsAsync(string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IToolCatalogStore store, CancellationToken token) => ListAsync("tools", cursor, limit, request, profiles, store, token);
    private static Task<IResult> ListPluginsAsync(string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IToolCatalogStore store, CancellationToken token) => ListAsync("plugins", cursor, limit, request, profiles, store, token);
    private static Task<IResult> ListMcpServersAsync(string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IToolCatalogStore store, CancellationToken token) => ListAsync("mcp-servers", cursor, limit, request, profiles, store, token);

    private static async Task<IResult> ListAsync(string resource, string? cursor, int? limit, HttpRequest request,
        ILocalProfileStore profiles, IToolCatalogStore store, CancellationToken token)
    {
        if (!TryPage(cursor, limit, out var size)) return InvalidCursor();
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        switch (resource)
        {
            case "skills": { var rows = await store.ListSkillsAsync(cursor, size + 1, token); var items = rows.Take(size).Select(ToContract).ToArray(); return Results.Ok(new SkillPage(items, rows.Count > size ? items[^1].Id : null)); }
            case "tools": { var rows = await store.ListToolsAsync(cursor, size + 1, token); var items = rows.Take(size).Select(ToContract).ToArray(); return Results.Ok(new ToolPage(items, rows.Count > size ? items[^1].Id : null)); }
            case "plugins": { var rows = await store.ListPluginsAsync(cursor, size + 1, token); var items = rows.Take(size).Select(ToContract).ToArray(); return Results.Ok(new PluginPage(items, rows.Count > size ? items[^1].Id : null)); }
            default: { var rows = await store.ListMcpServersAsync(cursor, size + 1, token); var items = rows.Take(size).Select(ToContract).ToArray(); return Results.Ok(new McpServerPage(items, rows.Count > size ? items[^1].Id : null)); }
        }
    }

    private static Task<IResult> GetSkillAsync(string id, HttpRequest request, ILocalProfileStore profiles, IToolCatalogStore store, CancellationToken token) => GetAsync("skills", id, request, profiles, store, token);
    private static Task<IResult> GetToolAsync(string id, HttpRequest request, ILocalProfileStore profiles, IToolCatalogStore store, CancellationToken token) => GetAsync("tools", id, request, profiles, store, token);
    private static Task<IResult> GetPluginAsync(string id, HttpRequest request, ILocalProfileStore profiles, IToolCatalogStore store, CancellationToken token) => GetAsync("plugins", id, request, profiles, store, token);
    private static Task<IResult> GetMcpServerAsync(string id, HttpRequest request, ILocalProfileStore profiles, IToolCatalogStore store, CancellationToken token) => GetAsync("mcp-servers", id, request, profiles, store, token);

    private static async Task<IResult> GetAsync(string resource, string id, HttpRequest request,
        ILocalProfileStore profiles, IToolCatalogStore store, CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _)) return InvalidId(resource);
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        ComponentCatalogRecord? row = resource switch
        {
            "skills" => await store.GetSkillAsync(id, token),
            "tools" => await store.GetToolAsync(id, token),
            "plugins" => await store.GetPluginAsync(id, token),
            _ => await store.GetMcpServerAsync(id, token),
        };
        return row is null ? NotFound(resource) : Results.Ok(ToContract(row));
    }

    private static Task<IResult> PatchSkillAsync(string id, ComponentPatchRequest input, HttpRequest request, ILocalProfileStore profiles, IToolCatalogStore store, IClock clock, CancellationToken token) => PatchAsync("skills", id, input, request, profiles, store, clock, token);
    private static Task<IResult> PatchToolAsync(string id, ComponentPatchRequest input, HttpRequest request, ILocalProfileStore profiles, IToolCatalogStore store, IClock clock, CancellationToken token) => PatchAsync("tools", id, input, request, profiles, store, clock, token);
    private static Task<IResult> PatchPluginAsync(string id, ComponentPatchRequest input, HttpRequest request, ILocalProfileStore profiles, IToolCatalogStore store, IClock clock, CancellationToken token) => PatchAsync("plugins", id, input, request, profiles, store, clock, token);
    private static Task<IResult> PatchMcpServerAsync(string id, ComponentPatchRequest input, HttpRequest request, ILocalProfileStore profiles, IToolCatalogStore store, IClock clock, CancellationToken token) => PatchAsync("mcp-servers", id, input, request, profiles, store, clock, token);

    private static async Task<IResult> PatchAsync(string resource, string id, ComponentPatchRequest input,
        HttpRequest request, ILocalProfileStore profiles, IToolCatalogStore store, IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _)) return InvalidId(resource);
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        try
        {
            var row = await store.UpdateAsync(new(profile.TenantId, profile.Id, resource, id,
                input.State, input.Endpoint, clock.UtcNow), token);
            return Results.Ok(ToContract(row));
        }
        catch (ToolCatalogNotFoundException e) { return NotFound(e.Resource); }
        catch (ToolCatalogValidationException e) { return Problem(400, "invalid_component_update", e.Message); }
    }

    private static object ToContract(ComponentCatalogRecord row) => row switch
    {
        SkillCatalogRecord value => ToContract(value),
        ToolCatalogRecord value => ToContract(value),
        PluginCatalogRecord value => ToContract(value),
        McpServerCatalogRecord value => ToContract(value),
        _ => throw new InvalidOperationException("Unknown component record."),
    };
    private static SkillContract ToContract(SkillCatalogRecord value) => new(value.Id, value.Key, value.Name, value.Description, value.Version, value.State);
    private static ToolContract ToContract(ToolCatalogRecord value) => new(value.Id, value.Key, value.Name, value.Description, value.Kind, value.State);
    private static PluginContract ToContract(PluginCatalogRecord value) => new(value.Id, value.Key, value.Name, value.Version, value.Description, value.State, value.ProvidesToolIds);
    private static McpServerContract ToContract(McpServerCatalogRecord value) => new(value.Id, value.Name, value.Transport, value.Endpoint, value.State, value.ToolCount);
    private static bool TryPage(string? cursor, int? limit, out int size) { size = limit ?? 50; return size is >= 1 and <= 200 && (cursor is null || UlidValue.TryParse(cursor, out _)); }
    private static IResult InvalidCursor() => Problem(400, "invalid_cursor", "Cursor or limit is invalid.");
    private static IResult InvalidId(string resource) => Problem(400, "invalid_component_id", $"{resource} ID must be a ULID.");
    private static IResult SessionRequired() => Problem(401, "local_session_required", "A local profile session is required.");
    private static IResult NotFound(string resource) => Problem(404, "component_not_found", $"The {resource} component does not exist.");
    private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record ComponentPatchRequest(string? State, string? Endpoint);
public sealed record SkillContract(string Id, string Key, string Name, string Description, string Version, string State);
public sealed record ToolContract(string Id, string Key, string Name, string Description, string Kind, string State);
public sealed record PluginContract(string Id, string Key, string Name, string Version, string Description, string State, IReadOnlyList<string> ProvidesToolIds);
public sealed record McpServerContract(string Id, string Name, string Transport, string Endpoint, string State, int ToolCount);
public sealed record SkillPage(IReadOnlyList<SkillContract> Items, string? NextCursor);
public sealed record ToolPage(IReadOnlyList<ToolContract> Items, string? NextCursor);
public sealed record PluginPage(IReadOnlyList<PluginContract> Items, string? NextCursor);
public sealed record McpServerPage(IReadOnlyList<McpServerContract> Items, string? NextCursor);
