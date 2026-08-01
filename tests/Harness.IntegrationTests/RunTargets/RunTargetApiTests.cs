using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Realtime;
using Harness.Host.RunTargets;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.RunTargets;

public sealed class RunTargetApiTests
{
    private const string DotNetProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
        </Project>
        """;
    private const string DotNetProgram = """
        using System.Net;
        var url = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")!;
        using var listener = new HttpListener(); listener.Prefixes.Add(url + "/"); listener.Start();
        Console.WriteLine("dotnet-ready " + url);
        while (true) { var context = await listener.GetContextAsync(); var bytes = "dotnet-ok"u8.ToArray(); await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close(); }
        """;
    private const string NodePackage = """{"name":"node-api","main":"server.js"}""";
    private const string NodeProgram = """
        const http = require('http'); const port = Number(process.env.PORT);
        http.createServer((_req, res) => { res.end('node-ok'); }).listen(port, '127.0.0.1', () => console.log(`node-ready ${port}`));
        """;
    private const string PythonProgram = """
        import http.server, os
        port = int(os.environ["PORT"])
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                body = b"python-ok"
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            def log_message(self, *args):
                pass
        print(f"python-ready {port}", flush=True)
        http.server.HTTPServer(("127.0.0.1", port), Handler).serve_forever()
        """;

    [Fact]
    public async Task DetectsRunsRestartsStopsAndCleansRealDotNetAndNodeTargets()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)); var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"run-targets-{Guid.NewGuid():N}"); var workspace = Path.Combine(root, "workspace"); var database = Path.Combine(root, "run-targets.db"); Directory.CreateDirectory(Path.Combine(workspace, "dotnet")); Directory.CreateDirectory(Path.Combine(workspace, "node")); Directory.CreateDirectory(Path.Combine(workspace, "python"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "dotnet", "FixtureApi.csproj"), DotNetProject, timeout.Token); await File.WriteAllTextAsync(Path.Combine(workspace, "dotnet", "Program.cs"), DotNetProgram, timeout.Token); await File.WriteAllTextAsync(Path.Combine(workspace, "node", "package.json"), NodePackage, timeout.Token); await File.WriteAllTextAsync(Path.Combine(workspace, "node", "server.js"), NodeProgram, timeout.Token); await File.WriteAllTextAsync(Path.Combine(workspace, "python", "main.py"), PythonProgram, timeout.Token);
        var cookies = new CookieContainer(); string profileId; string projectId; string dotnetId; string nodeId;
        try
        {
            await using (var app = CreateHost(database))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = Address(app.Services); using (var anonymous = new HttpClient { BaseAddress = address }) using (var denied = await anonymous.GetAsync("/api/v1/run-targets", timeout.Token)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                    using var handler = new HttpClientHandler { CookieContainer = cookies }; using var client = new HttpClient(handler) { BaseAddress = address };
                    using (var response = await client.PostAsJsonAsync("/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR"), timeout.Token)) { response.EnsureSuccessStatusCode(); profileId = (await response.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token))!.Id; }
                    string organizationId; using (var response = await client.PostAsJsonAsync("/api/v1/organizations", new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, timeout.Token)) { response.EnsureSuccessStatusCode(); organizationId = (await response.Content.ReadFromJsonAsync<OrganizationResponse>(timeout.Token))!.Id; }
                    using (var response = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest { OrganizationId = organizationId, Name = "Runtime", Key = "RUNTIME", Description = "Runtime fixtures", RepositoryProvider = "local", RepositoryUrl = workspace }, timeout.Token)) { response.EnsureSuccessStatusCode(); projectId = (await response.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token))!.Id; }
                    using (var response = await client.PatchAsJsonAsync($"/api/v1/settings/{profileId}", new { workingDirectory = workspace }, timeout.Token)) response.EnsureSuccessStatusCode();

                    var page = (await client.GetFromJsonAsync<RunTargetPage>($"/api/v1/run-targets?projectId={projectId}", timeout.Token))!; Assert.Equal(3, page.Items.Count); var dotnet = page.Items.Single(x => x.Name.EndsWith("(.NET)", StringComparison.Ordinal)); var node = page.Items.Single(x => x.Name.EndsWith("(Node)", StringComparison.Ordinal)); var python = page.Items.Single(x => x.Name.EndsWith("(Python)", StringComparison.Ordinal)); dotnetId = dotnet.Id; nodeId = node.Id; var pythonId = python.Id; Assert.All(page.Items, target => { Assert.Equal("http", target.Kind); Assert.Equal("stopped", target.State); Assert.NotNull(target.Port); });

                    using (var response = await client.PostAsync($"/api/v1/run-targets/{nodeId}/start", null, timeout.Token)) { response.EnsureSuccessStatusCode(); Assert.Equal("running", (await response.Content.ReadFromJsonAsync<RunTargetContract>(timeout.Token))?.State); }
                    await WaitForBodyAsync(node.Url!, "node-ok", timeout.Token);
                    using (var response = await client.PostAsync($"/api/v1/run-targets/{dotnetId}/start", null, timeout.Token)) { response.EnsureSuccessStatusCode(); Assert.Equal("running", (await response.Content.ReadFromJsonAsync<RunTargetContract>(timeout.Token))?.State); }
                    await WaitForBodyAsync(dotnet.Url!, "dotnet-ok", timeout.Token);
                    using (var response = await client.PostAsync($"/api/v1/run-targets/{pythonId}/start", null, timeout.Token)) { response.EnsureSuccessStatusCode(); Assert.Equal("running", (await response.Content.ReadFromJsonAsync<RunTargetContract>(timeout.Token))?.State); }
                    await WaitForBodyAsync(python.Url!, "python-ok", timeout.Token);
                    foreach (var runningId in new[] { nodeId, dotnetId, pythonId })
                    {
                        var health = (await client.GetFromJsonAsync<RunTargetHealthContract>($"/api/v1/run-targets/{runningId}/health", timeout.Token))!;
                        Assert.True(health.Healthy); Assert.Equal(200, health.StatusCode); Assert.Equal("http_endpoint_responded", health.Detail);
                    }
                    using (var response = await client.PostAsync($"/api/v1/run-targets/{nodeId}/restart", null, timeout.Token)) { response.EnsureSuccessStatusCode(); Assert.Equal("running", (await response.Content.ReadFromJsonAsync<RunTargetContract>(timeout.Token))?.State); }
                    await WaitForBodyAsync(node.Url!, "node-ok", timeout.Token);
                    using (var response = await client.PostAsync($"/api/v1/run-targets/{dotnetId}/stop", null, timeout.Token)) { response.EnsureSuccessStatusCode(); Assert.Equal("stopped", (await response.Content.ReadFromJsonAsync<RunTargetContract>(timeout.Token))?.State); }
                    var stoppedHealth = (await client.GetFromJsonAsync<RunTargetHealthContract>($"/api/v1/run-targets/{dotnetId}/health", timeout.Token))!; Assert.False(stoppedHealth.Healthy); Assert.Equal("process_not_running", stoppedHealth.Detail);
                    using (var response = await client.PostAsync($"/api/v1/projects/{projectId}/run-environment/cleanup", null, timeout.Token)) { response.EnsureSuccessStatusCode(); Assert.Equal(2, await response.Content.ReadFromJsonAsync<int>(timeout.Token)); }

                    var stopped = (await client.GetFromJsonAsync<RunTargetPage>($"/api/v1/run-targets?projectId={projectId}", timeout.Token))!; Assert.All(stopped.Items, target => Assert.Equal("stopped", target.State)); var snapshots = await WaitForEventsAsync(client, projectId, timeout.Token); Assert.Contains(snapshots.Project.Delta, item => item.Type == "run.logAppended" && item.Payload.GetProperty("line").GetString()!.Contains("node-ready", StringComparison.Ordinal)); Assert.Contains(snapshots.Global.Delta, item => item.Type == "audit.eventAppended" && item.Payload.GetProperty("auditEvent").GetProperty("action").GetString() == "run.environmentCleaned");
                }
                finally { await app.StopAsync(timeout.Token); }
            }
            await using var restarted = CreateHost(database); await restarted.StartAsync(timeout.Token);
            try { using var client = new HttpClient { BaseAddress = Address(restarted.Services) }; client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}"); var values = (await client.GetFromJsonAsync<RunTargetPage>($"/api/v1/run-targets?projectId={projectId}", timeout.Token))!; Assert.Equal(3, values.Items.Count); Assert.All(values.Items, value => Assert.Equal("stopped", value.State)); Assert.Contains(values.Items, value => value.Id == dotnetId); Assert.Contains(values.Items, value => value.Id == nodeId); }
            finally { await restarted.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task WaitForBodyAsync(string url, string expected, CancellationToken token) { using var client = new HttpClient(); for (var i = 0; i < 200; i++) { try { if (await client.GetStringAsync(url, token) == expected) return; } catch (HttpRequestException) { } await Task.Delay(50, token); } throw new TimeoutException($"Service {url} did not become ready."); }
    private static async Task<(EventStreamSnapshot Project, EventStreamSnapshot Global)> WaitForEventsAsync(HttpClient client, string projectId, CancellationToken token) { for (var i = 0; i < 200; i++) { var project = await client.GetFromJsonAsync<EventStreamSnapshot>($"/api/v1/event-streams/snapshot?stream=project:{projectId}", token); var global = await client.GetFromJsonAsync<EventStreamSnapshot>("/api/v1/event-streams/snapshot?stream=global", token); if (project is not null && global is not null && project.Delta.Any(x => x.Type == "run.logAppended" && x.Payload.GetProperty("line").GetString()!.Contains("node-ready", StringComparison.Ordinal)) && global.Delta.Any(x => x.Type == "audit.eventAppended" && x.Payload.GetProperty("auditEvent").GetProperty("action").GetString() == "run.environmentCleaned")) return (project, global); await Task.Delay(25, token); } throw new TimeoutException("Run target events were not dispatched."); }
    private static WebApplication CreateHost(string database) => HostApplication.Build(["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
    private static Uri Address(IServiceProvider services) { var values = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? throw new InvalidOperationException("No address."); return new Uri(values.Single(x => x.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))); }
}
