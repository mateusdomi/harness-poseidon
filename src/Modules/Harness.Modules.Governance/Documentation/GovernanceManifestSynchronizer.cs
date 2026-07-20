using System.Security.Cryptography;

namespace Harness.Modules.Governance.Documentation;

public sealed class GovernanceManifestSynchronizer
{
    private readonly string _repositoryRoot;
    private readonly GovernanceManifestService _manifestService;

    public GovernanceManifestSynchronizer(string repositoryRoot)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _manifestService = new GovernanceManifestService(_repositoryRoot);
    }

    public int Synchronize(DateTimeOffset now)
    {
        var manifest = _manifestService.LoadAndValidate();
        var changed = 0;
        foreach (var document in manifest.Documents)
        {
            var path = Resolve(document.Path);
            if (!File.Exists(path))
            {
                continue;
            }

            var checksum = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))}";
            var tokenEstimate = EstimateTokens(File.ReadAllText(path));
            if (!string.Equals(document.Checksum, checksum, StringComparison.Ordinal))
            {
                document.Checksum = checksum;
                changed++;
            }

            if (document.TokenEstimate != tokenEstimate)
            {
                document.TokenEstimate = tokenEstimate;
                changed++;
            }
        }

        manifest.LastGeneratedAt = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        _manifestService.Save(manifest);
        return changed;
    }

    public static int EstimateTokens(string content)
    {
        var words = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return Math.Max(1, (int)Math.Ceiling(words * 1.5m));
    }

    private string Resolve(string relativePath) => Path.Combine(_repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
