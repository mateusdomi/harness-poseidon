using System.Net;
using Harness.SharedKernel.RunnerIpc;

namespace Harness.Host.Ipc;

public static class RunnerIpcEndpoint
{
    public static IEndpointRouteBuilder MapRunnerIpc(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/internal/runner/messages",
                (HttpContext context,
                    RunnerMessageEnvelope message,
                    RunnerIpcToken token,
                    RunnerIpcMessageProcessor processor) => Process(context, message, token, processor))
            .ExcludeFromDescription();
        return endpoints;
    }

    private static IResult Process(
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

        var result = processor.Process(message);
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
