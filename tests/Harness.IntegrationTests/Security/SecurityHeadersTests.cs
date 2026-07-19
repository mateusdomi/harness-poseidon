using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Profiles;
using Harness.Host.Security;
using Harness.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Security;

public sealed class SecurityHeadersTests
{
    [Fact]
    public async Task HostEmitsSecurityHeadersWithSecureDefaultCorsAndSessionCookie()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"security-headers-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "security.db");
        Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = Address(app.Services) };

                // A API carrega os cabeçalhos e, sobre HTTP loopback, não emite HSTS.
                client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
                using var health = await client.GetAsync("/health", timeout.Token);
                health.EnsureSuccessStatusCode();
                Assert.Equal("nosniff", Single(health, "X-Content-Type-Options"));
                Assert.Equal("DENY", Single(health, "X-Frame-Options"));
                Assert.Equal("no-referrer", Single(health, "Referrer-Policy"));
                Assert.Equal("same-origin", Single(health, "Cross-Origin-Opener-Policy"));
                Assert.Equal("same-origin", Single(health, "Cross-Origin-Resource-Policy"));
                Assert.Contains("camera=()", Single(health, "Permissions-Policy"), StringComparison.Ordinal);
                Assert.False(
                    health.Headers.Contains("Strict-Transport-Security"),
                    "HSTS não deve ser emitido sobre HTTP loopback.");

                var csp = Single(health, "Content-Security-Policy");
                Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
                Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
                Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
                Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);

                // Sem CORS permissivo por padrão: origem externa não recebe ACAO.
                Assert.False(
                    health.Headers.Contains("Access-Control-Allow-Origin"),
                    "Nenhuma política CORS permissiva deve estar habilitada.");

                // A SPA estática também recebe a CSP (o middleware é o primeiro do pipeline).
                using var index = await client.GetAsync("/", timeout.Token);
                index.EnsureSuccessStatusCode();
                Assert.Contains(
                    "default-src 'self'",
                    Single(index, "Content-Security-Policy"),
                    StringComparison.Ordinal);

                // Cookie de sessão: HttpOnly + SameSite=Strict e, sob HTTP, sem Secure.
                using var created = await client.PostAsJsonAsync(
                    "/api/v1/profiles",
                    new CreateProfileRequest("Mateus", "mateus@example.com", null, "pt-BR"),
                    timeout.Token);
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                var setCookie = created.Headers.GetValues("Set-Cookie").Single();
                Assert.Contains("harness.profile=", setCookie, StringComparison.Ordinal);
                Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MiddlewareEmitsHstsOnlyOverHttps(bool isHttps)
    {
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask, new SecurityHeadersOptions());
        var context = new DefaultHttpContext();
        context.Request.Scheme = isHttps ? "https" : "http";

        await middleware.InvokeAsync(context);

        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal(
            isHttps,
            context.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public async Task DisabledMiddlewareEmitsNoHeaders()
    {
        var middleware = new SecurityHeadersMiddleware(
            _ => Task.CompletedTask, new SecurityHeadersOptions { Enabled = false });
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.False(context.Response.Headers.ContainsKey("Content-Security-Policy"));
        Assert.False(context.Response.Headers.ContainsKey("X-Content-Type-Options"));
    }

    private static string Single(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values)
            ? values.Single()
            : throw new InvalidOperationException($"Cabeçalho ausente: {header}.");

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value => value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
