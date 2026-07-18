using System.Net;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
}

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new HealthResponse("healthy")));

app.Run();

internal sealed record HealthResponse(string Status);

public partial class Program;
