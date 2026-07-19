using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Modules.Organizations.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Harness.IntegrationTests.Auth;

/// <summary>
/// Maquinaria OIDC completa contra um provedor de identidade fake local
/// (discovery + JWKS + tokens RS256): tudo que não depende de uma conta real
/// do Entra ID é provado aqui; o smoke com o tenant real usa a mesma
/// configuração trocando somente Authority/Audience.
/// </summary>
public sealed class OidcServerModeHostTests
{
    [Fact]
    public async Task OidcModeRequiresBearerAndProvisionsAdminThenMembers()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"oidc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "harness-test-key" };

        await using var idp = BuildFakeIdentityProvider(rsa);
        await idp.StartAsync(timeout.Token);
        var issuer = Address(idp.Services).ToString().TrimEnd('/');

        try
        {
            await using var app = HostApplication.Build(
            [
                "--urls",
                "http://127.0.0.1:0",
                "--Harness:DatabasePath",
                Path.Combine(root, "harness.db"),
                "--Harness:Auth:Oidc:Enabled",
                "true",
                "--Harness:Auth:Oidc:Authority",
                issuer,
                "--Harness:Auth:Oidc:Audience",
                "harness-api",
                "--Harness:Auth:Oidc:RequireHttpsMetadata",
                "false",
            ]);
            await app.StartAsync(timeout.Token);
            try
            {
                var address = Address(app.Services);
                using var anonymous = new HttpClient { BaseAddress = address };

                // /health permanece aberto; a API inteira exige bearer válido.
                using (var health = await anonymous.GetAsync(
                    new Uri("/health", UriKind.Relative), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, health.StatusCode);
                }

                using (var unauthorized = await anonymous.GetAsync(
                    new Uri("/api/v1/profiles/current", UriKind.Relative), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                }

                using var alice = Authenticated(
                    address, issuer, signingKey, "alice-subject", "Alice", "alice@poseidon.local");
                var aliceProfile = (await alice.GetFromJsonAsync<ProfileResponse>(
                    "/api/v1/profiles/current", timeout.Token))!;
                Assert.Equal("Alice", aliceProfile.DisplayName);
                var aliceAgain = (await alice.GetFromJsonAsync<ProfileResponse>(
                    "/api/v1/profiles/current", timeout.Token))!;
                Assert.Equal(aliceProfile.Id, aliceAgain.Id);

                using var bob = Authenticated(
                    address, issuer, signingKey, "bob-subject", "Bob", null);
                var bobProfile = (await bob.GetFromJsonAsync<ProfileResponse>(
                    "/api/v1/profiles/current", timeout.Token))!;
                Assert.NotEqual(aliceProfile.Id, bobProfile.Id);

                // Primeiro subject autenticado vira admin do tenant; o segundo adere como member.
                var store = app.Services.GetRequiredService<ILocalProfileStore>();
                var aliceRecord = (await store.GetByExternalSubjectAsync(
                    "alice-subject", timeout.Token))!;
                var bobRecord = (await store.GetByExternalSubjectAsync(
                    "bob-subject", timeout.Token))!;
                Assert.Equal(LocalProfileRole.Admin, aliceRecord.Role);
                Assert.Equal(LocalProfileRole.Member, bobRecord.Role);
                Assert.Equal(aliceRecord.TenantId, bobRecord.TenantId);

                // RBAC/ABAC continuam valendo sobre a sessão OIDC.
                using (var hijack = await bob.PatchAsJsonAsync(
                    $"/api/v1/profiles/{aliceProfile.Id}",
                    new Dictionary<string, string> { ["displayName"] = "Invasor" },
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Forbidden, hijack.StatusCode);
                }

                using (var adminPatch = await alice.PatchAsJsonAsync(
                    $"/api/v1/profiles/{bobProfile.Id}",
                    new Dictionary<string, string> { ["displayName"] = "Bob Renomeado" },
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, adminPatch.StatusCode);
                }

                // Member autenticado trabalha normalmente no tenant compartilhado.
                using var organization = await bob.PostAsJsonAsync(
                    "/api/v1/organizations",
                    new CreateOrganizationRequest { Name = "OIDC", Slug = "oidc" },
                    timeout.Token);
                Assert.Equal(HttpStatusCode.Created, organization.StatusCode);

                // Token de audiência errada é recusado.
                using var wrongAudience = new HttpClient { BaseAddress = address };
                wrongAudience.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Bearer", MintToken(issuer, signingKey, "eve", "Eve", null, "other-api"));
                using (var rejected = await wrongAudience.GetAsync(
                    new Uri("/api/v1/profiles/current", UriKind.Relative), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
                }
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            await idp.StopAsync(timeout.Token);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static WebApplication BuildFakeIdentityProvider(RSA rsa)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var idp = builder.Build();
        var parameters = rsa.ExportParameters(false);
        idp.MapGet("/.well-known/openid-configuration", (HttpRequest request) =>
        {
            var baseUrl = $"http://{request.Host}";
            return Results.Json(new
            {
                issuer = baseUrl,
                jwks_uri = $"{baseUrl}/jwks",
            });
        });
        idp.MapGet("/jwks", () => Results.Json(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = "harness-test-key",
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent),
                },
            },
        }));
        return idp;
    }

    private static HttpClient Authenticated(
        Uri address,
        string issuer,
        RsaSecurityKey signingKey,
        string subject,
        string name,
        string? email)
    {
        var client = new HttpClient { BaseAddress = address };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", MintToken(issuer, signingKey, subject, name, email, "harness-api"));
        return client;
    }

    private static string MintToken(
        string issuer,
        RsaSecurityKey signingKey,
        string subject,
        string name,
        string? email,
        string audience)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["name"] = name,
        };
        if (email is not null)
        {
            claims["email"] = email;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
        });
    }

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value =>
            value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
