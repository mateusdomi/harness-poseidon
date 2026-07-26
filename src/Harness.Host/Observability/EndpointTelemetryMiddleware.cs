using System.Diagnostics;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Routing;

namespace Harness.Host.Observability;

internal sealed class EndpointTelemetryMiddleware(RequestDelegate next)
{
    private static readonly Dictionary<string, string> CorrelationKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tenantId"] = "tenant_id",
            ["projectId"] = "project_id",
            ["conversationId"] = "conversation_id",
            ["workTaskId"] = "work_task_id",
            ["taskId"] = "work_task_id",
            ["executionId"] = "execution_id",
            ["attemptId"] = "attempt_id",
            ["agentId"] = "agent_id",
            ["toolCallId"] = "tool_call_id",
            ["modelInvocationId"] = "model_invocation_id",
            ["turnId"] = "chief_turn_id",
        };

    private readonly RequestDelegate _next =
        next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await _next(context);
        }
        finally
        {
            Enrich(Activity.Current, context);
        }
    }

    internal static void Enrich(Activity? activity, HttpContext context)
    {
        if (activity is null)
        {
            return;
        }

        if (context.GetEndpoint() is RouteEndpoint routeEndpoint)
        {
            activity.SetTag("http.route", routeEndpoint.RoutePattern.RawText);
        }

        foreach (var routeValue in context.Request.RouteValues)
        {
            if (!CorrelationKeys.TryGetValue(routeValue.Key, out var tagName) ||
                routeValue.Value?.ToString() is not { } value ||
                !UlidValue.TryParse(value, out _))
            {
                continue;
            }

            activity.SetTag(tagName, value);
        }

        SensitiveTelemetryProcessor.Sanitize(activity);
    }
}
