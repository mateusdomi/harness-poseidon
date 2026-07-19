using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Licensing;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Licensing.Domain;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Licensing;

public sealed class SignedLicenseApiTests
{
    [Fact]
    public async Task ActivatesVerifiesRevokesOfflineAndNeverBlocksDataAfterExpiry()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"signed-license-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "license.db");
        var cookies = new CookieContainer();
        Directory.CreateDirectory(root);
        var (publicKey, privateKey) = LicenseSigning.GenerateKeyPair();

        try
        {
            await using var app = HostApplication.Build(
            [
                "--urls",
                "http://127.0.0.1:0",
                "--Harness:DatabasePath",
                database,
                "--Harness:Licensing:PublicKey",
                publicKey,
            ]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                await CreateProfileAsync(client, timeout.Token);
                var organization = await CreateOrganizationAsync(client, timeout.Token);
                var project = await CreateProjectAsync(client, organization.Id, timeout.Token);

                using (var missing = await client.GetAsync(
                    new Uri("/api/v1/licenses/signed", UriKind.Relative), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                }

                // Documento emitido "offline" pelo emissor e ativado sem nenhuma rede.
                var expiredIssue = DateTimeOffset.UtcNow.AddYears(-2);
                var expiredDocument = new LicenseDocument(
                    UlidValue.New(expiredIssue).ToString(),
                    "Poseidon",
                    "Mateus Software Architect",
                    null,
                    expiredIssue,
                    expiredIssue.AddYears(1),
                    30,
                    ["backend", "frontend", "agents"],
                    1);
                var expiredSignature = LicenseSigning.Sign(expiredDocument, privateKey);
                using (var response = await client.PostAsJsonAsync(
                    "/api/v1/licenses/signed/activation",
                    new SignedLicenseActivationRequest(
                        ToPayload(expiredDocument),
                        expiredSignature),
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                    var contract = (await response.Content
                        .ReadFromJsonAsync<SignedLicenseContract>(timeout.Token))!;
                    Assert.Equal("expired", contract.State);
                }

                // Expirada NUNCA bloqueia leitura dos dados.
                using (var projects = await client.GetAsync(
                    new Uri($"/api/v1/projects/{project.Id}", UriKind.Relative), timeout.Token))
                {
                    projects.EnsureSuccessStatusCode();
                }

                // Documento adulterado é rejeitado e auditado.
                var issue = DateTimeOffset.UtcNow;
                var document = new LicenseDocument(
                    UlidValue.New(issue).ToString(),
                    "Poseidon",
                    "Mateus Software Architect",
                    null,
                    issue,
                    issue.AddYears(1),
                    30,
                    ["backend", "frontend", "agents", "workflows", "channels"],
                    1);
                var signature = LicenseSigning.Sign(document, privateKey);
                using (var tampered = await client.PostAsJsonAsync(
                    "/api/v1/licenses/signed/activation",
                    new SignedLicenseActivationRequest(
                        ToPayload(document with { Licensee = "Pirata" }),
                        signature),
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
                }

                using (var response = await client.PostAsJsonAsync(
                    "/api/v1/licenses/signed/activation",
                    new SignedLicenseActivationRequest(ToPayload(document), signature),
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                    var contract = (await response.Content
                        .ReadFromJsonAsync<SignedLicenseContract>(timeout.Token))!;
                    Assert.Equal("active", contract.State);
                    Assert.Equal(5, contract.Entitlements.Count);
                }

                // Revogação por lista assinada: importa, muda o estado, e a reativação é recusada.
                var revocationList = new LicenseRevocationList(
                    [document.LicenseId],
                    DateTimeOffset.UtcNow);
                var revocationSignature = LicenseSigning.Sign(revocationList, privateKey);
                using (var badList = await client.PostAsJsonAsync(
                    "/api/v1/licenses/signed/revocations",
                    new SignedRevocationImportRequest(
                        new RevocationListPayload(
                            revocationList.RevokedLicenseIds,
                            revocationList.IssuedAt.AddMinutes(1)),
                        revocationSignature),
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, badList.StatusCode);
                }

                using (var response = await client.PostAsJsonAsync(
                    "/api/v1/licenses/signed/revocations",
                    new SignedRevocationImportRequest(
                        new RevocationListPayload(
                            revocationList.RevokedLicenseIds,
                            revocationList.IssuedAt),
                        revocationSignature),
                    timeout.Token))
                {
                    response.EnsureSuccessStatusCode();
                    Assert.Equal(1, await response.Content.ReadFromJsonAsync<int>(timeout.Token));
                }

                var revoked = (await client.GetFromJsonAsync<SignedLicenseContract>(
                    "/api/v1/licenses/signed", timeout.Token))!;
                Assert.Equal("revoked", revoked.State);
                using (var reactivation = await client.PostAsJsonAsync(
                    "/api/v1/licenses/signed/activation",
                    new SignedLicenseActivationRequest(ToPayload(document), signature),
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, reactivation.StatusCode);
                }

                // Revogada também não bloqueia leitura dos dados.
                using var stillReadable = await client.GetAsync(
                    new Uri($"/api/v1/projects/{project.Id}", UriKind.Relative), timeout.Token);
                stillReadable.EnsureSuccessStatusCode();
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
                Directory.Delete(root, true);
            }
        }
    }

    private static LicenseDocumentPayload ToPayload(LicenseDocument document) => new(
        document.LicenseId,
        document.Product,
        document.Licensee,
        document.DeviceFingerprint,
        document.IssuedAt,
        document.ExpiresAt,
        document.GraceDays,
        document.Entitlements,
        document.Version);

    private static async Task<ProfileResponse> CreateProfileAsync(
        HttpClient client,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/profiles",
            new CreateProfileRequest("Mateus", null, null, "pt-BR"),
            token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileResponse>(token))!;
    }

    private static async Task<OrganizationResponse> CreateOrganizationAsync(
        HttpClient client,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" },
            token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationResponse>(token))!;
    }

    private static async Task<ProjectResponse> CreateProjectAsync(
        HttpClient client,
        string organizationId,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = "Poseidon",
                Key = "POSEIDON",
                Description = "Backend",
            },
            token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>(token))!;
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
