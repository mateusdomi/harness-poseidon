using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Identity;

namespace Harness.Host.GovernanceDocs;

/// <summary>
/// API dos documentos de governança/documentação em disco (working tree).
/// Exige sessão local. Todo path é validado pelo <see cref="GovernanceDocsService"/>
/// (allowlist rígido + bloqueio de traversal/symlink).
/// </summary>
public static class GovernanceDocsEndpoints
{
    public static IEndpointRouteBuilder MapGovernanceDocs(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/governance-docs").WithTags("governance-docs");
        group.MapGet("/", ListAsync)
            .Produces<GovernanceDocTreeContract>().ProducesProblem(401);
        group.MapGet("/{**path}", GetAsync)
            .Produces<GovernanceDocContent>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapPut("/{**path}", PutAsync)
            .Produces<GovernanceDocContent>().ProducesProblem(400).ProducesProblem(401);
        group.MapDelete("/{**path}", DeleteAsync)
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        HttpRequest request, ILocalProfileStore profiles, GovernanceDocsService service,
        CancellationToken token)
    {
        if (await Session(request, profiles, token) is null) return Unauthorized();
        return Results.Ok(new GovernanceDocTreeContract(service.Tree(), GovernanceDocsService.AllowedRoots));
    }

    private static async Task<IResult> GetAsync(
        string path, HttpRequest request, ILocalProfileStore profiles, GovernanceDocsService service,
        CancellationToken token)
    {
        if (await Session(request, profiles, token) is null) return Unauthorized();
        try
        {
            return Results.Ok(await service.ReadAsync(path, token));
        }
        catch (GovernanceDocNotFoundException exception) { return NotFound(exception.Message); }
        catch (GovernanceDocPathException exception) { return BadRequest(exception.Message); }
    }

    private static async Task<IResult> PutAsync(
        string path, SaveGovernanceDocRequest input, HttpRequest request, ILocalProfileStore profiles,
        GovernanceDocsService service, CancellationToken token)
    {
        if (await Session(request, profiles, token) is null) return Unauthorized();
        try
        {
            return Results.Ok(await service.WriteAsync(path, input.Content ?? string.Empty, token));
        }
        catch (GovernanceDocPathException exception) { return BadRequest(exception.Message); }
    }

    private static async Task<IResult> DeleteAsync(
        string path, HttpRequest request, ILocalProfileStore profiles, GovernanceDocsService service,
        CancellationToken token)
    {
        if (await Session(request, profiles, token) is null) return Unauthorized();
        try
        {
            service.Delete(path);
            return Results.NoContent();
        }
        catch (GovernanceDocNotFoundException exception) { return NotFound(exception.Message); }
        catch (GovernanceDocPathException exception) { return BadRequest(exception.Message); }
    }

    private static Task<LocalProfileRecord?> Session(
        HttpRequest request, ILocalProfileStore profiles, CancellationToken token) =>
        LocalProfileSession.ResolveAsync(request, profiles, token);

    private static IResult Unauthorized() =>
        Results.Problem(statusCode: 401, title: "local_session_required",
            detail: "A local profile session is required.");

    private static IResult NotFound(string detail) =>
        Results.Problem(statusCode: 404, title: "governance_doc_not_found", detail: detail);

    private static IResult BadRequest(string detail) =>
        Results.Problem(statusCode: 400, title: "invalid_governance_doc_path", detail: detail);
}

public sealed record GovernanceDocTreeContract(
    IReadOnlyList<GovernanceDocFile> Files, IReadOnlyList<string> Roots);

public sealed record SaveGovernanceDocRequest(string? Content);
