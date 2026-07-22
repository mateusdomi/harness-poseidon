namespace Harness.Host.Agents;

/// <summary>
/// Contexto de uma continuação governada, resolvido a partir de um artifact arquivado e
/// validado. Viaja no <see cref="StartAgentRunCommand"/> para que a execução prepare a
/// worktree com o diff anterior, detecte patch stale e preserve a proveniência.
///
/// A base NUNCA é um ref Git arbitrário fornecido pelo cliente: é uma referência interna
/// governada (o <c>origin/develop</c> atual, resolvido pelo servidor).
/// </summary>
public sealed record ContinuationContext
{
    /// <summary>Tentativa anterior reprovada de onde esta continuação parte.</summary>
    public required string ResumeFromAttemptId { get; init; }

    /// <summary>Commit do worker que originou o patch arquivado.</summary>
    public required string SourceCommit { get; init; }

    /// <summary>Path absoluto do patch arquivado, já validado por checksum.</summary>
    public required string PatchPath { get; init; }

    /// <summary>Checksum validado do patch.</summary>
    public required string PatchSha256 { get; init; }

    public string? ReviewId { get; init; }

    public string? ReceiptTurnId { get; init; }

    /// <summary>Achados do critic anterior, já formatados como critérios de aceite.</summary>
    public IReadOnlyList<string> PriorFindings { get; init; } = [];
}

/// <summary>Decisão tipada da política de continuação.</summary>
public sealed record ContinuationDecision(
    bool Allowed,
    string ReasonCode,
    IReadOnlyList<string> PriorFindings)
{
    public static ContinuationDecision Reject(string reasonCode) => new(false, reasonCode, []);
}

/// <summary>
/// Política pura da continuação governada. Decide se um artifact arquivado pode originar uma
/// nova tentativa, validando identidade (tenant, projeto, tarefa, papel, actor), repositório
/// controlado, veredito e escopo — sem IO, para ser testável e determinística.
///
/// Invariantes:
/// - só se continua o que foi <c>fail</c>: reabrir trabalho aprovado seria refazê-lo à toa;
/// - o mesmo papel e o mesmo actor: uma continuação não troca de dono nem de escopo;
/// - o mesmo repositório controlado: um patch não é portável entre árvores arbitrárias;
/// - o escopo do arquivo não pode exceder o escopo do papel: nada de escalar claim.
/// </summary>
public static class ContinuationPolicy
{
    public sealed record Request(
        string Role,
        string ActorAlias,
        string ProjectId,
        string TaskId,
        string RepositoryRoot,
        IReadOnlyList<string> RolePathScopes);

    public static ContinuationDecision Evaluate(ArchivedAttemptManifest manifest, Request request)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(manifest.Verdict, "fail", StringComparison.OrdinalIgnoreCase))
        {
            return ContinuationDecision.Reject("continuation.verdict_not_fail");
        }

        if (!string.Equals(manifest.ProjectId, request.ProjectId, StringComparison.Ordinal))
        {
            return ContinuationDecision.Reject("continuation.project_mismatch");
        }

        if (!string.Equals(manifest.TaskId, request.TaskId, StringComparison.Ordinal))
        {
            return ContinuationDecision.Reject("continuation.task_mismatch");
        }

        if (!string.Equals(manifest.Role, request.Role, StringComparison.OrdinalIgnoreCase))
        {
            return ContinuationDecision.Reject("continuation.role_mismatch");
        }

        if (!string.Equals(manifest.ActorAlias, request.ActorAlias, StringComparison.OrdinalIgnoreCase))
        {
            return ContinuationDecision.Reject("continuation.actor_mismatch");
        }

        if (!PathsEqual(manifest.ControlledRepositoryRoot, request.RepositoryRoot))
        {
            return ContinuationDecision.Reject("continuation.repository_mismatch");
        }

        // O escopo do arquivo precisa estar contido no escopo do papel corrente. Um patch
        // que reivindicava mais do que o papel concede hoje não pode ser continuado às cegas.
        foreach (var claim in manifest.ScopeClaims)
        {
            if (!request.RolePathScopes.Contains(claim, StringComparer.Ordinal))
            {
                return ContinuationDecision.Reject("continuation.scope_escalation");
            }
        }

        var criteria = manifest.Findings
            .Select(finding => $"[{finding.Severity} {finding.Code}] {finding.Summary}")
            .ToArray();
        return new ContinuationDecision(true, "continuation.allowed", criteria);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.Ordinal);
}
