using Harness.Host.Security;
using Microsoft.Extensions.Configuration;

namespace Harness.UnitTests.Security;

public sealed class ServerSecretReferenceResolverTests
{
    [Fact]
    public void ResolvesMountedVaultSecretWithoutAllowingTraversal()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "poseidon-secret-resolver",
            Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "database");
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllText(Path.Combine(nested, "connection-string"), "resolved-value\n");
            var resolver = new MountedVaultSecretReferenceResolver(root);

            Assert.Equal(
                "resolved-value",
                resolver.Resolve("secret://database/connection-string"));
            Assert.Null(resolver.Resolve("secret://../outside"));
            Assert.Null(resolver.Resolve("env://POSEIDON_POSTGRES_CONNECTION"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FactoryComposesEnvironmentAndOptionalVaultResolvers()
    {
        var variable = $"POSEIDON_SECRET_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, "environment-value");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection()
                .Build();
            var resolver = ServerSecretReferenceResolverFactory.Create(configuration);

            Assert.Equal("environment-value", resolver.Resolve($"env://{variable}"));
            Assert.Null(resolver.Resolve("secret://not-configured"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}
