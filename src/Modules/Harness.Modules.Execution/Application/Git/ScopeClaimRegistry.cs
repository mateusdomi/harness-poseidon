using Harness.Modules.Execution.Domain.Git;

namespace Harness.Modules.Execution.Application.Git;

public sealed class ScopeClaimRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IReadOnlyList<ScopeClaim>> _claimsByAttempt =
        new(StringComparer.Ordinal);

    public ScopeClaimAcquisition TryAcquire(string attemptId, IEnumerable<ScopeClaim> claims)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        ArgumentNullException.ThrowIfNull(claims);

        var requested = claims.Distinct().OrderBy(claim => claim.PathPattern, StringComparer.Ordinal).ToArray();
        if (requested.Length == 0)
        {
            throw new ArgumentException("At least one scope claim is required.", nameof(claims));
        }

        lock (_sync)
        {
            if (_claimsByAttempt.TryGetValue(attemptId, out var existingForAttempt))
            {
                if (existingForAttempt.SequenceEqual(requested))
                {
                    return new ScopeClaimAcquisition(true, []);
                }

                throw new InvalidOperationException("An active attempt cannot replace its scope claims.");
            }

            var conflicts = _claimsByAttempt
                .SelectMany(pair =>
                    requested.SelectMany(requestedClaim =>
                        pair.Value
                            .Where(existingClaim => requestedClaim.Intersects(existingClaim))
                            .Select(existingClaim => new ScopeClaimConflict(
                                pair.Key,
                                requestedClaim,
                                existingClaim))))
                .ToArray();

            if (conflicts.Length != 0)
            {
                return new ScopeClaimAcquisition(false, conflicts);
            }

            _claimsByAttempt.Add(attemptId, requested);
            return new ScopeClaimAcquisition(true, []);
        }
    }

    public bool Release(string attemptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        lock (_sync)
        {
            return _claimsByAttempt.Remove(attemptId);
        }
    }
}
