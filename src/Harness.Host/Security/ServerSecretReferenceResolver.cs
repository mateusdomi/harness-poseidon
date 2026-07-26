using Harness.Host.Notifications;

namespace Harness.Host.Security;

public static class ServerSecretReferenceResolverFactory
{
    public static ISecretReferenceResolver Create(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var resolvers = new List<ISecretReferenceResolver>
        {
            new EnvironmentSecretReferenceResolver(),
        };
        var vaultMountPath = configuration["Harness:Secrets:VaultMountPath"];
        if (!string.IsNullOrWhiteSpace(vaultMountPath))
        {
            resolvers.Add(new MountedVaultSecretReferenceResolver(vaultMountPath));
        }

        return new CompositeSecretReferenceResolver(resolvers);
    }
}

public sealed class CompositeSecretReferenceResolver(
    IEnumerable<ISecretReferenceResolver> resolvers) : ISecretReferenceResolver
{
    private readonly ISecretReferenceResolver[] _resolvers =
        resolvers?.ToArray() ?? throw new ArgumentNullException(nameof(resolvers));

    public string? Resolve(string reference)
    {
        foreach (var resolver in _resolvers)
        {
            var resolved = resolver.Resolve(reference);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }
}

/// <summary>
/// Resolves <c>secret://</c> references from files rendered by a Vault Agent.
/// The configured directory is the only readable root; traversal and symbolic-link
/// components are rejected before any file is opened.
/// </summary>
public sealed class MountedVaultSecretReferenceResolver : ISecretReferenceResolver
{
    private readonly string _mountRoot;
    private readonly string _mountPrefix;

    public MountedVaultSecretReferenceResolver(string mountRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountRoot);
        _mountRoot = Path.GetFullPath(mountRoot);
        _mountPrefix = _mountRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _mountRoot
            : _mountRoot + Path.DirectorySeparatorChar;
    }

    public string? Resolve(string reference)
    {
        const string scheme = "secret://";
        if (string.IsNullOrWhiteSpace(reference) ||
            !reference.StartsWith(scheme, StringComparison.Ordinal))
        {
            return null;
        }

        var locator = reference[scheme.Length..];
        var segments = locator.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 ||
            segments.Any(segment =>
                segment is "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine([_mountRoot, .. segments]));
        if (!candidate.StartsWith(_mountPrefix, StringComparison.Ordinal) ||
            !File.Exists(candidate) ||
            ContainsSymbolicLink(candidate))
        {
            return null;
        }

        return File.ReadAllText(candidate).TrimEnd('\r', '\n');
    }

    private bool ContainsSymbolicLink(string candidate)
    {
        var relative = Path.GetRelativePath(_mountRoot, candidate);
        var current = _mountRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
