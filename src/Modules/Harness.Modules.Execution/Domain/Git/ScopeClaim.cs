namespace Harness.Modules.Execution.Domain.Git;

public sealed record ScopeClaim
{
    public ScopeClaim(string pathPattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathPattern);
        if (Path.IsPathRooted(pathPattern))
        {
            throw new ArgumentException("A scope claim must be workspace-relative.", nameof(pathPattern));
        }

        var normalized = pathPattern.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 ||
            normalized.Split('/').Any(segment => segment is "." or ".." or "") ||
            (normalized.Contains('*') && !normalized.EndsWith("/**", StringComparison.Ordinal)) ||
            normalized[..Math.Max(0, normalized.Length - 3)].Contains('*'))
        {
            throw new ArgumentException(
                "A scope claim must contain normalized path segments and may use only a trailing /** wildcard.",
                nameof(pathPattern));
        }

        PathPattern = normalized;
        BasePath = normalized.EndsWith("/**", StringComparison.Ordinal)
            ? normalized[..^3].TrimEnd('/')
            : normalized;
    }

    public string PathPattern { get; }

    public string BasePath { get; }

    public bool Intersects(ScopeClaim other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(BasePath, other.BasePath, StringComparison.OrdinalIgnoreCase) ||
            IsDescendant(BasePath, other.BasePath) ||
            IsDescendant(other.BasePath, BasePath);
    }

    private static bool IsDescendant(string candidate, string ancestor) =>
        candidate.StartsWith($"{ancestor}/", StringComparison.OrdinalIgnoreCase);
}
