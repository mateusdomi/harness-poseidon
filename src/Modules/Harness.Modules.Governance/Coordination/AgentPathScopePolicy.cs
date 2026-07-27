namespace Harness.Modules.Governance.Coordination;

/// <summary>
/// Escopo de paths por PAPEL LÓGICO, nunca por provider (CA-1). O papel
/// `FrontendSpecialist` pode ser executado por qualquer executor autorizado — Codex,
/// Kimi Code ou outro — sem que a governança precise conhecer a marca do CLI.
/// </summary>
public enum AgentPathScopeKind
{
    Backend = 0,

    /// <summary>Papel lógico de frontend: `frontend/**` e `docs/frontend/**`.</summary>
    FrontendSpecialist = 1,

    /// <summary>
    /// Alias histórico de <see cref="FrontendSpecialist"/>. Mantido para compatibilidade de
    /// configuração e código existentes; o papel nunca foi propriedade de um provider.
    /// </summary>
    Kimi = FrontendSpecialist,
}

public sealed record AgentPathScopeDecision(
    bool Allowed,
    string Code,
    IReadOnlyList<string> RejectedClaims);

public static class AgentPathScopePolicy
{
    // Raízes do papel de frontend. Pertencem ao PAPEL, não a um provider.
    private static readonly string[] FrontendRoots = ["frontend", "docs/frontend"];

    private static readonly string[] BackendRoots =
    [
        "src",
        "tests",
        "infra",
        "tools/backend",
        "docs/backend",
        "docs/contracts",
        "docs/architecture",
        "docs/decisions",
        "docs/security",
        "docs/testing",
    ];

    private static readonly string[] SharedRoots = ["governance", ".github"];

    /// <summary>
    /// As DUAS fontes canônicas. Elas são a regra que restringe o agente — deixar o restringido
    /// reescrever a própria restrição é o clássico confused deputy, e nenhuma outra trava do
    /// sistema sobrevive a isso. Alterá-las é ato humano, nunca card.
    /// </summary>
    private static readonly string[] CanonicalSources =
    [
        "governance/core.md",
        "governance/manifest.yaml",
    ];

    /// <summary>
    /// Raiz cuja varredura ampla (<c>governance/**</c>) engoliria as fontes canônicas. Um claim
    /// mais estreito dentro dela continua válido — é assim que um card produz um documento de
    /// governança sem ganhar poder sobre o próprio guardrail.
    /// </summary>
    private const string CanonicalRoot = "governance";

    private static readonly string[] SharedFiles =
    [
        "AGENTS.md",
        "CLAUDE.md",
        "README.md",
        "Harness.sln",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
    ];

    public static AgentPathScopeDecision Evaluate(
        AgentPathScopeKind kind,
        IReadOnlyList<string> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (claims.Count == 0)
        {
            return new AgentPathScopeDecision(false, "agent_path_scope_empty", []);
        }

        var rejected = claims
            .Where(claim => !IsAllowed(kind, claim))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return rejected.Length == 0
            ? new AgentPathScopeDecision(true, "agent_path_scope_allowed", [])
            : new AgentPathScopeDecision(false, "agent_path_scope_denied", rejected);
    }

    private static bool IsAllowed(AgentPathScopeKind kind, string claim)
    {
        if (!TryNormalize(claim, out var normalized, out var basePath))
        {
            return false;
        }

        if (kind == AgentPathScopeKind.FrontendSpecialist)
        {
            return FrontendRoots.Any(root => IsWithin(basePath, root));
        }

        if (FrontendRoots.Any(root => IsWithin(basePath, root) || IsWithin(root, basePath)))
        {
            return false;
        }

        if (BackendRoots.Any(root => IsWithin(basePath, root)))
        {
            return true;
        }

        // Fonte canônica é INEGOCIÁVEL: nem o arquivo em si, nem uma varredura ampla que o
        // engula. Sem esta negação, `governance/**` entrava pelo caminho de raiz compartilhada e
        // um card comum podia reescrever a regra que o restringe.
        if (CanonicalSources.Contains(normalized, StringComparer.OrdinalIgnoreCase) ||
            string.Equals(basePath, CanonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (SharedRoots.Any(root => IsWithin(basePath, root)))
        {
            return true;
        }

        return !normalized.EndsWith("/**", StringComparison.Ordinal) &&
            SharedFiles.Contains(basePath, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryNormalize(string claim, out string normalized, out string basePath)
    {
        normalized = string.Empty;
        basePath = string.Empty;
        if (string.IsNullOrWhiteSpace(claim) || Path.IsPathRooted(claim))
        {
            return false;
        }

        normalized = claim.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/');
        if (normalized.Length == 0 ||
            segments.Any(segment => segment is "" or "." or "..") ||
            (normalized.Contains('*') && !normalized.EndsWith("/**", StringComparison.Ordinal)) ||
            normalized[..Math.Max(0, normalized.Length - 3)].Contains('*'))
        {
            return false;
        }

        basePath = normalized.EndsWith("/**", StringComparison.Ordinal)
            ? normalized[..^3].TrimEnd('/')
            : normalized;
        return basePath.Length > 0;
    }

    private static bool IsWithin(string candidate, string root) =>
        string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith($"{root}/", StringComparison.OrdinalIgnoreCase);
}
