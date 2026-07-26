using System.Diagnostics;
using Harness.Host.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Harness.IntegrationTests.Observability;

public sealed class EndpointTelemetryTests
{
    private const string ProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FAX";

    [Fact]
    public async Task MiddlewareAddsOnlyCanonicalRouteAndOpaqueCorrelationIds()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?access_token=secret");
        context.Request.RouteValues["projectId"] = ProjectId;
        context.Request.RouteValues["displayName"] = "secret-person";
        context.SetEndpoint(new RouteEndpoint(
            static _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/v1/projects/{projectId}"),
            order: 0,
            EndpointMetadataCollection.Empty,
            "project-by-id"));
        using var activity = new Activity("http-request").Start();
        activity.SetTag(
            "url.full",
            $"https://example.test/api/v1/projects/{ProjectId}?access_token=secret");
        var middleware = new EndpointTelemetryMiddleware(static _ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Equal("/api/v1/projects/{projectId}", activity.GetTagItem("http.route"));
        Assert.Equal(ProjectId, activity.GetTagItem("project_id"));
        Assert.Null(activity.GetTagItem("url.full"));
        Assert.Null(activity.GetTagItem("display_name"));
        Assert.DoesNotContain(
            activity.TagObjects,
            tag => tag.Value?.ToString()?.Contains("secret", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void EnrichmentRejectsNonUlidRouteValues()
    {
        var context = new DefaultHttpContext();
        context.Request.RouteValues["projectId"] = "person@example.test";
        using var activity = new Activity("http-request").Start();

        EndpointTelemetryMiddleware.Enrich(activity, context);

        Assert.Null(activity.GetTagItem("project_id"));
    }
}
