using System.Net;
using Harness.Host.Realtime;
using Harness.SharedKernel.Time;

namespace Harness.Host;

public static class HostApplication
{
    public static WebApplication Build(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = WebApplication.CreateBuilder(args);

        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
        {
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        }

        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddSingleton<EventStreamStore>();
        builder.Services.AddSingleton<EventPublisher>();
        builder.Services.AddSignalR(options => options.EnableDetailedErrors = builder.Environment.IsDevelopment());
        builder.Services.AddOpenApi(options =>
            options.AddDocumentTransformer(
                (document, _, _) =>
                {
                    document.Info.Title = "Harness API";
                    document.Servers?.Clear();
                    return Task.CompletedTask;
                }));

        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok(new HealthResponse("healthy")))
            .WithTags("system");
        app.MapOpenApi("/openapi/{documentName}.json");
        app.MapHub<EventsHub>("/hubs/events");
        app.MapGet(
            "/api/v1/event-streams/snapshot",
            IResult (string? stream, long? afterSequence, EventStreamStore store) =>
            {
                if (!EventStreamName.IsValid(stream))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "invalid_event_stream",
                        detail: "A valid stream query parameter is required.");
                }

                var cursor = afterSequence ?? 0;
                if (cursor < 0)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "invalid_event_sequence",
                        detail: "afterSequence cannot be negative.");
                }

                return Results.Ok(store.ReadSnapshot(stream!, cursor));
            })
            .WithTags("events")
            .Produces<EventStreamSnapshot>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }

    private sealed record HealthResponse(string Status);
}
