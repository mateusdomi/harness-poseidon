namespace Harness.Host.Operations;

/// <summary>
/// No modo servidor (PostgreSQL) o backup/restore local do SQLite não se aplica:
/// os endpoints permanecem no contrato, respondendo com orientação explícita para
/// a estratégia de backup do banco gerenciado.
/// </summary>
public static class ServerOperationsEndpoints
{
    public static IEndpointRouteBuilder MapServerOperationsUnavailable(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/backups", ServerModeProblem).WithTags("operations");
        endpoints.MapPost("/api/v1/backups/{id}/restore", ServerModeProblem).WithTags("operations");
        endpoints.MapGet("/api/v1/diagnostics", ServerModeProblem).WithTags("operations");
        return endpoints;
    }

    private static IResult ServerModeProblem() => Results.Problem(
        statusCode: 409,
        title: "server_mode_operations",
        detail: "No modo servidor, backup/restore e diagnóstico local são responsabilidade " +
                "da operação do PostgreSQL (pg_dump/PITR); os endpoints locais aplicam-se " +
                "somente ao modo pessoal SQLite.");
}
