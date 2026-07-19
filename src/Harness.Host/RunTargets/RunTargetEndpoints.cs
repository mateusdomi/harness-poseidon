using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Notifications;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.RunTargets;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.RunTargets;

public static class RunTargetEndpoints
{
    public static IEndpointRouteBuilder MapRunTargets(this IEndpointRouteBuilder endpoints)
    {
        var targets = endpoints.MapGroup("/api/v1/run-targets").WithTags("run-project");
        targets.MapGet("/", ListAsync).Produces<RunTargetPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        targets.MapGet("/{id}", GetAsync).Produces<RunTargetContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        targets.MapPost("/{id}/start", StartAsync).Produces<RunTargetContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        targets.MapPost("/{id}/stop", StopAsync).Produces<RunTargetContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        targets.MapPost("/{id}/restart", RestartAsync).Produces<RunTargetContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        targets.MapGet("/{id}/health", HealthAsync).Produces<RunTargetHealthContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        endpoints.MapPost("/api/v1/projects/{projectId}/run-environment/cleanup", CleanupAsync).WithTags("run-project").Produces<int>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string? projectId,
        string? cursor,
        int? limit,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        INotificationStore settingsStore,
        IRunTargetStore store,
        RunTargetDetector detector,
        RunTargetProcessSupervisor supervisor,
        IClock clock,
        CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token); if (session is null) return Unauthorized();
        if (projectId is not null && !UlidValue.TryParse(projectId, out _) || cursor is not null && !UlidValue.TryParse(cursor, out _) || limit is < 1 or > 200) return Invalid("invalid_run_target_query", "Run target query is invalid.");
        if (projectId is not null)
        {
            var project = await projects.GetAsync(session.TenantId, projectId, token); if (project is null) return Missing("project");
            var settings = await settingsStore.GetSettingsAsync(session.TenantId, session.Id, session.Id, token);
            if (!string.IsNullOrWhiteSpace(settings?.WorkingDirectory))
            {
                try
                {
                    var root = ResolveProjectRoot(settings.WorkingDirectory, project);
                    var definitions = await detector.DetectAsync(root, token);
                    var synchronized = await store.SynchronizeAsync(new(session.TenantId, session.Id, projectId, definitions, clock.UtcNow), token);
                    foreach (var stale in synchronized.Where(value => value.State == "running" && !supervisor.IsRunning(value.Id)))
                        await store.SetStateAsync(new(session.TenantId, session.Id, stale.Id, "unknown", "A persisted running service has no live supervised process after restart.", clock.UtcNow), token);
                }
                catch (RunTargetValidationException e) { return Invalid("invalid_run_environment", e.Message); }
            }
        }
        var size = limit ?? 50; var rows = await store.ListAsync(session.TenantId, projectId, cursor, size + 1, token); var items = rows.Take(size).Select(ToContract).ToArray(); return Results.Ok(new RunTargetPage(items, rows.Count > size ? items[^1].Id : null));
    }

    private static async Task<IResult> GetAsync(string id, HttpRequest request, ILocalProfileStore profiles, IRunTargetStore store, CancellationToken token)
    { if (!UlidValue.TryParse(id, out _)) return InvalidId(); var session = await LocalProfileSession.ResolveAsync(request, profiles, token); if (session is null) return Unauthorized(); var value = await store.GetAsync(session.TenantId, id, token); return value is null ? Missing("run_target") : Results.Ok(ToContract(value)); }

    private static Task<IResult> StartAsync(string id, HttpRequest request, ILocalProfileStore profiles, INotificationStore settings, IRunTargetStore store, RunTargetProcessSupervisor supervisor, IClock clock, CancellationToken token) => StartCoreAsync(id, false, request, profiles, settings, store, supervisor, clock, token);
    private static Task<IResult> RestartAsync(string id, HttpRequest request, ILocalProfileStore profiles, INotificationStore settings, IRunTargetStore store, RunTargetProcessSupervisor supervisor, IClock clock, CancellationToken token) => StartCoreAsync(id, true, request, profiles, settings, store, supervisor, clock, token);
    private static async Task<IResult> StartCoreAsync(string id, bool restart, HttpRequest request, ILocalProfileStore profiles, INotificationStore settingsStore, IRunTargetStore store, RunTargetProcessSupervisor supervisor, IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _)) return InvalidId(); var session = await LocalProfileSession.ResolveAsync(request, profiles, token); if (session is null) return Unauthorized(); var settings = await settingsStore.GetSettingsAsync(session.TenantId, session.Id, session.Id, token); var launch = await store.GetLaunchAsync(session.TenantId, id, token); if (launch is null) return Missing("run_target");
        var authorization = Authorize(settings, launch); if (authorization is not null) return authorization;
        try
        {
            if (restart)
            {
                await supervisor.StopTargetAsync(id, token);
                await store.SetStateAsync(new(session.TenantId, session.Id, id, "stopped", $"Restarting \"{launch.Target.Name}\": previous process stopped.", clock.UtcNow), token);
            }
            else if (supervisor.IsRunning(id)) return Results.Ok(ToContract((await store.GetAsync(session.TenantId, id, token))!));
            var pid = await supervisor.StartTargetAsync(session.TenantId, session.Id, launch, token);
            var value = await store.SetStateAsync(new(session.TenantId, session.Id, id, "running", $"Started \"{launch.Target.Name}\" as managed process {pid}.", clock.UtcNow), token); return Results.Ok(ToContract(value));
        }
        catch (RunTargetValidationException e) { return Problem(409, "run_target_start_failed", e.Message); }
        catch { await supervisor.StopTargetAsync(id, CancellationToken.None); throw; }
    }

    private static async Task<IResult> StopAsync(string id, HttpRequest request, ILocalProfileStore profiles, IRunTargetStore store, RunTargetProcessSupervisor supervisor, IClock clock, CancellationToken token)
    { if (!UlidValue.TryParse(id, out _)) return InvalidId(); var session = await LocalProfileSession.ResolveAsync(request, profiles, token); if (session is null) return Unauthorized(); var current = await store.GetAsync(session.TenantId, id, token); if (current is null) return Missing("run_target"); await supervisor.StopTargetAsync(id, token); var value = await store.SetStateAsync(new(session.TenantId, session.Id, id, "stopped", $"Stopped \"{current.Name}\".", clock.UtcNow), token); return Results.Ok(ToContract(value)); }

    private static async Task<IResult> CleanupAsync(string projectId, HttpRequest request, ILocalProfileStore profiles, IProjectStore projects, IRunTargetStore store, RunTargetProcessSupervisor supervisor, IClock clock, CancellationToken token)
    { if (!UlidValue.TryParse(projectId, out _)) return Invalid("invalid_project_id", "Project ID must be a ULID."); var session = await LocalProfileSession.ResolveAsync(request, profiles, token); if (session is null) return Unauthorized(); if (await projects.GetAsync(session.TenantId, projectId, token) is null) return Missing("project"); await supervisor.StopProjectAsync(projectId, token); return Results.Ok(await store.CleanupAsync(new(session.TenantId, session.Id, projectId, clock.UtcNow), token)); }

    private static async Task<IResult> HealthAsync(
        string id,
        HttpRequest request,
        ILocalProfileStore profiles,
        IRunTargetStore store,
        RunTargetProcessSupervisor supervisor,
        IClock clock,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _)) return InvalidId();
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        var target = await store.GetAsync(session.TenantId, id, token);
        if (target is null) return Missing("run_target");
        var checkedAt = clock.UtcNow;
        RunTargetHealthContract health;
        if (target.State != "running" || !supervisor.IsRunning(target.Id))
        {
            health = new RunTargetHealthContract(
                target.Id, target.Url, false, null, "process_not_running", checkedAt);
        }
        else if (target.Kind != "http" || string.IsNullOrWhiteSpace(target.Url))
        {
            health = new RunTargetHealthContract(
                target.Id, target.Url, true, null, "process_alive_without_http_probe", checkedAt);
        }
        else
        {
            health = await ProbeAsync(target, checkedAt, token);
        }

        await store.MarkCheckedAsync(session.TenantId, target.Id, checkedAt, token);
        return Results.Ok(health);
    }

    private static async Task<RunTargetHealthContract> ProbeAsync(
        RunTargetRecord target,
        DateTimeOffset checkedAt,
        CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.GetAsync(new Uri(target.Url!), token);
            return new RunTargetHealthContract(
                target.Id,
                target.Url,
                true,
                (int)response.StatusCode,
                "http_endpoint_responded",
                checkedAt);
        }
        catch (HttpRequestException)
        {
            return new RunTargetHealthContract(
                target.Id, target.Url, false, null, "http_endpoint_unreachable", checkedAt);
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            return new RunTargetHealthContract(
                target.Id, target.Url, false, null, "http_endpoint_timeout", checkedAt);
        }
    }

    private static IResult? Authorize(SettingsRecord? settings, RunTargetLaunchRecord launch)
    {
        if (settings?.UnsafeModeAcceptedAt is null) return Problem(409, "unsafe_mode_acceptance_required", "Local project execution requires explicit unsafe-mode acceptance until a sandbox is configured.");
        if (string.IsNullOrWhiteSpace(settings.WorkingDirectory)) return Problem(409, "working_directory_required", "A working directory must be configured.");
        var root = Path.GetFullPath(settings.WorkingDirectory); var working = Path.GetFullPath(launch.WorkingDirectory); var relative = Path.GetRelativePath(root, working);
        return relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? Problem(409, "run_target_outside_working_directory", "The run target is outside the authorized working directory.") : null;
    }

    private static string ResolveProjectRoot(string configuredRoot, ProjectRecord project)
    {
        var root = Path.GetFullPath(configuredRoot);
        if (!Directory.Exists(root)) throw new RunTargetValidationException("The configured working directory does not exist.");
        if (project.RepositoryProvider == "local" && !string.IsNullOrWhiteSpace(project.RepositoryUrl) && Path.IsPathRooted(project.RepositoryUrl))
        {
            var repository = Path.GetFullPath(project.RepositoryUrl); var relative = Path.GetRelativePath(root, repository);
            if (relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathRooted(relative) && Directory.Exists(repository)) return repository;
        }
        var keyed = Path.Combine(root, project.Key); return Directory.Exists(keyed) ? keyed : root;
    }

    private static RunTargetContract ToContract(RunTargetRecord value) => new(value.Id, value.ProjectId, value.Name, value.Kind, value.Url, value.Port, value.State, value.DetectedAt, value.LastCheckAt);
    private static IResult InvalidId() => Invalid("invalid_run_target_id", "Run target ID must be a ULID."); private static IResult Unauthorized() => Problem(401, "local_session_required", "A local profile session is required."); private static IResult Missing(string resource) => Problem(404, $"{resource}_not_found", $"The {resource} does not exist."); private static IResult Invalid(string title, string detail) => Problem(400, title, detail); private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record RunTargetContract(string Id, string ProjectId, string Name, string Kind, string? Url, int? Port, string State, DateTimeOffset DetectedAt, DateTimeOffset? LastCheckAt);
public sealed record RunTargetPage(IReadOnlyList<RunTargetContract> Items, string? NextCursor);
public sealed record RunTargetHealthContract(string TargetId, string? Url, bool Healthy, int? StatusCode, string Detail, DateTimeOffset CheckedAt);
