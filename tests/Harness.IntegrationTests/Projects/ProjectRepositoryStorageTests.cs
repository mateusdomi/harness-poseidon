using Harness.Host.Projects;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Projects;

public sealed class ProjectRepositoryStorageTests
{
    [Fact]
    public async Task MissingRepositoryIsInitializedUnderTheControlledTenantRoot()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var root = Path.Combine(
            Path.GetTempPath(),
            "harness-tests",
            $"project-repositories-{Guid.NewGuid():N}");
        var tenantId = UlidValue.New(DateTimeOffset.UtcNow).ToString();

        try
        {
            var storage = new ProjectRepositoryStorage(root);

            var repository = await storage.EnsureInitializedAsync(
                tenantId,
                "PORTAL-CLIENTE",
                timeout.Token);
            var replay = await storage.EnsureInitializedAsync(
                tenantId,
                "PORTAL-CLIENTE",
                timeout.Token);

            Assert.Equal(repository, replay);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, tenantId, "portal-cliente")),
                repository);
            Assert.True(Directory.Exists(Path.Combine(repository, ".git")));
            Assert.True(Path.IsPathRooted(repository));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
