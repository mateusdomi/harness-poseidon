using System.Net;
using Harness.SharedKernel.RunnerIpc;

namespace Harness.Host.Ipc;

public static class RunnerIpcEndpoint
{
    public static IEndpointRouteBuilder MapRunnerIpc(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/_runner/ipc/status", (HttpContext context, RunnerIpcToken token) =>
        {
            if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None)) return Results.NotFound();
            var authorization = context.Request.Headers.Authorization.ToString();
            return authorization.StartsWith("Bearer ", StringComparison.Ordinal) &&
                   token.Matches(authorization["Bearer ".Length..])
                ? Results.Ok(new { status = "ready" })
                : Results.Unauthorized();
        }).ExcludeFromDescription();
        endpoints.MapPost(
                "/api/v1/internal/runner/messages",
                async (HttpContext context,
                    RunnerMessageEnvelope message,
                    RunnerIpcToken token,
                    RunnerIpcMessageProcessor processor) =>
                    await ProcessAsync(context, message, token, processor))
            .ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> ProcessAsync(
        HttpContext context,
        RunnerMessageEnvelope message,
        RunnerIpcToken token,
        RunnerIpcMessageProcessor processor)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "runner_ipc_loopback_required",
                detail: "Runner IPC is available only on loopback.");
        }

        const string bearerPrefix = "Bearer ";
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase) ||
            !token.Matches(authorization[bearerPrefix.Length..]))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "runner_ipc_unauthorized",
                detail: "Runner IPC authentication failed.");
        }

        var result = await processor.ProcessAsync(message, context.RequestAborted);
        if (result.Failure is not null)
        {
            return Results.Problem(
                statusCode: result.Failure.StatusCode,
                title: result.Failure.Title,
                detail: result.Failure.Detail);
        }

        return Results.Ok(result.Receipt);
    }
}
