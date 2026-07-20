using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Harness.Modules.Governance.Patching;

public sealed record HashlinePatchOptions
{
    public bool Enabled { get; init; } = true;
}

public sealed record HashlinePatchCommand(
    string TenantId,
    string ProjectId,
    string TurnId,
    string RelativePath,
    string ExpectedChecksum,
    string NewContent,
    DateTimeOffset OccurredAt);

public enum HashlinePatchStatus
{
    Applied,
    StaleRejected,
    Invalid,
}

public sealed record HashlinePatchResult(
    HashlinePatchStatus Status,
    string RelativePath,
    string ExpectedChecksum,
    string ActualChecksum,
    string? AppliedChecksum,
    string Action);

public sealed record HashlinePatchAuditRecord(
    string TenantId,
    string ProjectId,
    string TurnId,
    string RelativePath,
    HashlinePatchStatus Status,
    string ExpectedChecksum,
    string ActualChecksum,
    string? AppliedChecksum,
    DateTimeOffset OccurredAt);

public interface IHashlinePatchAuditSink
{
    Task AppendAsync(HashlinePatchAuditRecord record, CancellationToken cancellationToken = default);
}

public sealed class HashlinePatchService(
    string repositoryRoot,
    HashlinePatchOptions options,
    IHashlinePatchAuditSink auditSink)
{
    private readonly string _repositoryRoot = Path.GetFullPath(repositoryRoot);
    private readonly HashlinePatchOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IHashlinePatchAuditSink _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));

    public async Task<HashlinePatchResult> ApplyAsync(
        HashlinePatchCommand command,
        CancellationToken cancellationToken = default)
    {
        Validate(command);
        var path = Resolve(command.RelativePath);
        var existing = await File.ReadAllTextAsync(path, cancellationToken);
        var actual = Checksum(existing);
        if (_options.Enabled && !string.Equals(command.ExpectedChecksum, actual, StringComparison.Ordinal))
        {
            var rejected = new HashlinePatchResult(
                HashlinePatchStatus.StaleRejected,
                command.RelativePath,
                command.ExpectedChecksum,
                actual,
                null,
                "re-read/replan");
            await AuditAsync(command, rejected, cancellationToken);
            return rejected;
        }

        var appliedChecksum = Checksum(command.NewContent);
        await AtomicWriteAsync(path, command.NewContent, cancellationToken);
        var applied = new HashlinePatchResult(
            HashlinePatchStatus.Applied,
            command.RelativePath,
            command.ExpectedChecksum,
            actual,
            appliedChecksum,
            "applied");
        await AuditAsync(command, applied, cancellationToken);
        return applied;
    }

    public static string Checksum(string content) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))}";

    private async Task AuditAsync(
        HashlinePatchCommand command,
        HashlinePatchResult result,
        CancellationToken token) => await _auditSink.AppendAsync(
        new HashlinePatchAuditRecord(
            command.TenantId,
            command.ProjectId,
            command.TurnId,
            command.RelativePath,
            result.Status,
            result.ExpectedChecksum,
            result.ActualChecksum,
            result.AppliedChecksum,
            command.OccurredAt),
        token);

    private string Resolve(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Hashline patch path must be repository-relative.", nameof(relativePath));
        }

        var resolved = Path.GetFullPath(Path.Combine(_repositoryRoot, relativePath));
        if (!resolved.StartsWith($"{_repositoryRoot}{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("Hashline patch path escapes the repository.", nameof(relativePath));
        }

        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException("Hashline patch target does not exist.", relativePath);
        }

        return resolved;
    }

    private static async Task AtomicWriteAsync(
        string path,
        string content,
        CancellationToken token)
    {
        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.hashline-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), token);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Validate(HashlinePatchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TurnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RelativePath);
        if (command.ExpectedChecksum.Length != 71 || !command.ExpectedChecksum.StartsWith("sha256:", StringComparison.Ordinal))
        {
            throw new ArgumentException("Expected checksum must be a lowercase SHA-256 reference.", nameof(command));
        }
    }
}

public sealed record PatchBenchmarkResult(
    string Strategy,
    int EditSuccesses,
    int StaleRejections,
    int Retries,
    int EstimatedTokens,
    long DurationMicroseconds,
    int Regressions);

public static class HashlinePatchBenchmark
{
    public static IReadOnlyList<PatchBenchmarkResult> Run(int iterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        var fixtures = Enumerable.Range(0, iterations)
            .Select(index => new PatchBenchmarkFixture(
                $"line={index};version=1;{new string('a', 1024)}",
                $"line={index};version=2;{new string('b', 1024)}",
                $"line={index};concurrent=other;{new string('c', 1024)}"))
            .ToArray();
        var baselineStart = Stopwatch.GetTimestamp();
        var baselineSuccesses = 0;
        var baselineRetries = 0;
        var baselineTokens = 0;
        var baselineRegressions = 0;
        foreach (var fixture in fixtures)
        {
            var freshResult = fixture.Replacement;
            if (freshResult == fixture.Replacement) baselineSuccesses++;
            baselineTokens += EstimateTokens(fixture.Original, fixture.Replacement);

            // The current edit has no pre-write anchor. A concurrent update is overwritten and
            // can only be discovered after the fact, requiring a re-read/retry.
            var staleResult = fixture.Replacement;
            if (staleResult != fixture.ConcurrentContent) baselineRegressions++;
            baselineRetries++;
            baselineTokens += EstimateTokens(fixture.ConcurrentContent, fixture.Replacement);
        }
        var baseline = new PatchBenchmarkResult(
            "current-edit",
            baselineSuccesses,
            0,
            baselineRetries,
            baselineTokens,
            Math.Max(1, (long)Stopwatch.GetElapsedTime(baselineStart).TotalMicroseconds),
            baselineRegressions);
        var hashlineStart = Stopwatch.GetTimestamp();
        var hashlineSuccesses = 0;
        var hashlineStaleRejections = 0;
        var hashlineTokens = 0;
        foreach (var fixture in fixtures)
        {
            var expected = HashlinePatchService.Checksum(fixture.Original);
            if (expected == HashlinePatchService.Checksum(fixture.Original)) hashlineSuccesses++;
            hashlineTokens += EstimateAnchorTokens(expected, fixture.Replacement);
            if (expected != HashlinePatchService.Checksum(fixture.ConcurrentContent)) hashlineStaleRejections++;
            hashlineTokens += EstimateAnchorTokens(expected, fixture.Replacement);
        }
        var hashline = new PatchBenchmarkResult(
            "hashline",
            hashlineSuccesses,
            hashlineStaleRejections,
            0,
            hashlineTokens,
            Math.Max(1, (long)Stopwatch.GetElapsedTime(hashlineStart).TotalMicroseconds),
            0);
        return [baseline, hashline];
    }

    private static int EstimateTokens(string content, string replacement) =>
        Math.Max(1, (content.Length + replacement.Length + 3) / 4);

    private static int EstimateAnchorTokens(string checksum, string replacement) =>
        Math.Max(1, (checksum.Length + replacement.Length + 3) / 4);

    private sealed record PatchBenchmarkFixture(
        string Original,
        string Replacement,
        string ConcurrentContent);
}
