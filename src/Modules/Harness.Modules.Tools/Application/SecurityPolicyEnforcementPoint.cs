using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Harness.Modules.Tools.Application;

public enum CapabilityActorKind
{
    Chief,
    Specialist,
    Worker,
}

public enum CapabilityOperation
{
    ToolExecution,
    ExternalPublication,
}

public sealed record CapabilityGrantRequest(
    CapabilityActorKind ActorKind,
    string ActorId,
    string TenantId,
    string ProjectId,
    string CardId,
    string AttemptId,
    CapabilityOperation Operation,
    IReadOnlyList<string> AllowedToolIds,
    IReadOnlyList<string> AllowedResourceIds,
    IReadOnlyList<string> AllowedPathClaims,
    DateTimeOffset ExpiresAt,
    long FencingToken);

public sealed class CapabilityToken
{
    internal CapabilityToken(string id, string secret)
    {
        Id = id;
        Secret = secret;
    }

    public string Id { get; }

    internal string Secret { get; }

    public override string ToString() => "[REDACTED CAPABILITY]";
}

public sealed record CapabilityAuthorizationRequest(
    CapabilityActorKind ActorKind,
    string ActorId,
    string TenantId,
    string ProjectId,
    string CardId,
    string AttemptId,
    CapabilityOperation Operation,
    string? ToolId,
    string ResourceId,
    string? RelativePath,
    long FencingToken);

public sealed record CapabilityDecision(bool Allowed, string Code, string Detail)
{
    public static CapabilityDecision Permit() =>
        new(true, "capability_allowed", "The capability authorizes this operation.");

    public static CapabilityDecision Deny(string code, string detail) =>
        new(false, code, detail);
}

public sealed record CapabilityDecisionAuditRecord(
    string? CapabilityId,
    bool Allowed,
    string Code,
    CapabilityActorKind ActorKind,
    string ActorId,
    string TenantId,
    string ProjectId,
    string CardId,
    string AttemptId,
    CapabilityOperation Operation,
    string? ToolId,
    string ResourceId,
    string? RelativePath,
    DateTimeOffset OccurredAt);

public interface ICapabilityDecisionAuditSink
{
    ValueTask RecordAsync(
        CapabilityDecisionAuditRecord record,
        CancellationToken cancellationToken = default);
}

public sealed class SecurityPolicyEnforcementPoint
{
    private readonly ConcurrentDictionary<string, StoredCapabilityGrant> _grants =
        new(StringComparer.Ordinal);
    private readonly ICapabilityDecisionAuditSink _audit;
    private readonly TimeProvider _timeProvider;

    public SecurityPolicyEnforcementPoint(
        ICapabilityDecisionAuditSink audit,
        TimeProvider? timeProvider = null)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CapabilityToken Issue(CapabilityGrantRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateGrant(request);

        var capabilityId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var token = new CapabilityToken(capabilityId, secret);
        var stored = new StoredCapabilityGrant(
            capabilityId,
            request.ActorKind,
            request.ActorId,
            request.TenantId,
            request.ProjectId,
            request.CardId,
            request.AttemptId,
            request.Operation,
            [.. request.AllowedToolIds.Distinct(StringComparer.Ordinal)],
            [.. request.AllowedResourceIds.Distinct(StringComparer.Ordinal)],
            [.. request.AllowedPathClaims.Select(NormalizePathClaim).Distinct(StringComparer.OrdinalIgnoreCase)],
            request.ExpiresAt,
            request.FencingToken);

        if (!_grants.TryAdd(HashSecret(secret), stored))
        {
            throw new InvalidOperationException("Unable to issue a unique capability token.");
        }

        return token;
    }

    public bool Revoke(CapabilityToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return _grants.TryRemove(HashSecret(token.Secret), out _);
    }

    public async ValueTask<CapabilityDecision> AuthorizeAsync(
        CapabilityToken? token,
        CapabilityAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        StoredCapabilityGrant? grant = null;
        CapabilityDecision decision;
        if (token is null)
        {
            decision = CapabilityDecision.Deny(
                "capability_required",
                "A capability token is required.");
        }
        else if (!_grants.TryGetValue(HashSecret(token.Secret), out grant) ||
                 !string.Equals(grant.CapabilityId, token.Id, StringComparison.Ordinal))
        {
            decision = CapabilityDecision.Deny(
                "capability_invalid",
                "The capability token is unknown or revoked.");
        }
        else
        {
            decision = Evaluate(grant, request, _timeProvider.GetUtcNow());
        }

        await _audit.RecordAsync(
            new CapabilityDecisionAuditRecord(
                grant?.CapabilityId ?? token?.Id,
                decision.Allowed,
                decision.Code,
                request.ActorKind,
                request.ActorId,
                request.TenantId,
                request.ProjectId,
                request.CardId,
                request.AttemptId,
                request.Operation,
                request.ToolId,
                request.ResourceId,
                request.RelativePath,
                _timeProvider.GetUtcNow()),
            cancellationToken);
        return decision;
    }

    private static CapabilityDecision Evaluate(
        StoredCapabilityGrant grant,
        CapabilityAuthorizationRequest request,
        DateTimeOffset now)
    {
        if (now >= grant.ExpiresAt)
            return CapabilityDecision.Deny("capability_expired", "The capability token has expired.");
        if (request.ActorKind == CapabilityActorKind.Chief &&
            request.Operation == CapabilityOperation.ToolExecution)
            return CapabilityDecision.Deny(
                "chief_execution_denied",
                "The Chief profile cannot execute tools.");
        if (request.ActorKind != CapabilityActorKind.Chief &&
            request.Operation == CapabilityOperation.ExternalPublication)
            return CapabilityDecision.Deny(
                "publication_actor_denied",
                "Only the Chief profile can publish externally.");
        if (grant.ActorKind != request.ActorKind ||
            !Matches(grant.ActorId, request.ActorId))
            return CapabilityDecision.Deny("capability_actor_mismatch", "The capability actor does not match.");
        if (!Matches(grant.TenantId, request.TenantId))
            return CapabilityDecision.Deny("capability_tenant_mismatch", "The capability tenant does not match.");
        if (!Matches(grant.ProjectId, request.ProjectId))
            return CapabilityDecision.Deny("capability_project_mismatch", "The capability project does not match.");
        if (!Matches(grant.CardId, request.CardId))
            return CapabilityDecision.Deny("capability_card_mismatch", "The capability card does not match.");
        if (!Matches(grant.AttemptId, request.AttemptId))
            return CapabilityDecision.Deny("capability_attempt_mismatch", "The capability attempt does not match.");
        if (grant.Operation != request.Operation)
            return CapabilityDecision.Deny("capability_operation_mismatch", "The capability operation does not match.");
        if (grant.FencingToken != request.FencingToken)
            return CapabilityDecision.Deny("capability_fencing_mismatch", "The capability fencing token is stale.");
        if (!grant.AllowedResourceIds.Contains(request.ResourceId, StringComparer.Ordinal))
            return CapabilityDecision.Deny("capability_resource_denied", "The resource is outside the capability.");
        if (request.Operation == CapabilityOperation.ToolExecution &&
            (string.IsNullOrWhiteSpace(request.ToolId) ||
             !grant.AllowedToolIds.Contains(request.ToolId, StringComparer.Ordinal)))
            return CapabilityDecision.Deny("capability_tool_denied", "The tool is outside the capability.");
        if (request.RelativePath is not null &&
            !grant.AllowedPathClaims.Any(claim => ClaimContains(claim, request.RelativePath)))
            return CapabilityDecision.Deny("capability_path_denied", "The path is outside the capability.");

        return CapabilityDecision.Permit();
    }

    private void ValidateGrant(CapabilityGrantRequest request)
    {
        Require(request.ActorId, nameof(request.ActorId));
        Require(request.TenantId, nameof(request.TenantId));
        Require(request.ProjectId, nameof(request.ProjectId));
        Require(request.CardId, nameof(request.CardId));
        Require(request.AttemptId, nameof(request.AttemptId));
        if (request.FencingToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "A positive fencing token is required.");
        if (request.ExpiresAt <= _timeProvider.GetUtcNow())
            throw new ArgumentOutOfRangeException(nameof(request), "Capability expiry must be in the future.");
        if (request.AllowedResourceIds.Count == 0 ||
            request.AllowedResourceIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one valid resource is required.", nameof(request));
        if (request.Operation == CapabilityOperation.ToolExecution &&
            (request.AllowedToolIds.Count == 0 || request.AllowedToolIds.Any(string.IsNullOrWhiteSpace)))
            throw new ArgumentException("Tool execution requires at least one valid tool.", nameof(request));
        _ = request.AllowedPathClaims.Select(NormalizePathClaim).ToArray();
    }

    private static void Require(string value, string name) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);

    private static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static bool Matches(string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.Ordinal);

    private static string NormalizePathClaim(string claim)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claim);
        if (Path.IsPathRooted(claim))
            throw new ArgumentException("Capability paths must be workspace-relative.", nameof(claim));

        var normalized = claim.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/');
        if (normalized.Length == 0 ||
            segments.Any(segment => segment is "" or "." or "..") ||
            (normalized.Contains('*') && !normalized.EndsWith("/**", StringComparison.Ordinal)) ||
            normalized[..Math.Max(0, normalized.Length - 3)].Contains('*'))
        {
            throw new ArgumentException(
                "Capability paths must be normalized and may use only a trailing /** wildcard.",
                nameof(claim));
        }

        return normalized;
    }

    private static bool ClaimContains(string claim, string relativePath)
    {
        string normalizedPath;
        try
        {
            normalizedPath = NormalizeRelativePath(relativePath);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var basePath = claim.EndsWith("/**", StringComparison.Ordinal)
            ? claim[..^3].TrimEnd('/')
            : claim;
        return string.Equals(basePath, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
            (claim.EndsWith("/**", StringComparison.Ordinal) &&
             normalizedPath.StartsWith($"{basePath}/", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        var normalized = NormalizePathClaim(relativePath);
        if (normalized.Contains('*'))
            throw new ArgumentException("Invocation paths cannot contain wildcards.", nameof(relativePath));
        return normalized;
    }

    private sealed record StoredCapabilityGrant(
        string CapabilityId,
        CapabilityActorKind ActorKind,
        string ActorId,
        string TenantId,
        string ProjectId,
        string CardId,
        string AttemptId,
        CapabilityOperation Operation,
        IReadOnlyList<string> AllowedToolIds,
        IReadOnlyList<string> AllowedResourceIds,
        IReadOnlyList<string> AllowedPathClaims,
        DateTimeOffset ExpiresAt,
        long FencingToken);
}
