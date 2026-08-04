namespace Harness.Modules.Workflows.Product;

/// <summary>
/// O conjunto de evidências de uma entrega, amarrado ao commit exato sobre o qual foi colhido.
/// É o input real do <see cref="ProductDeliveryGate"/>.
/// </summary>
public sealed record ProductDeliveryEvidence(
    string CommitSha,
    IReadOnlyList<ProductEvidence> Items,
    IReadOnlyList<string> Collectors)
{
    public static ProductDeliveryEvidence Empty(string commitSha) => new(commitSha, [], []);

    /// <summary>Resumo por tipo e força, para log e recibo — sem despejar os detalhes.</summary>
    public string Summary() => Items.Count == 0
        ? "no_product_evidence"
        : string.Join(
            ' ',
            Items
                .OrderBy(item => item.Kind)
                .Select(item =>
                    $"{item.Kind.ToString().ToLowerInvariant()}=" +
                    $"{(item.Satisfied ? "ok" : "fail")}/{item.Level.ToString().ToLowerInvariant()}"));
}

/// <summary>
/// Uma verificação executada FORA deste módulo (build, suíte de testes, jornada E2E, smoke da API)
/// cujo resultado o Poseidon guardou. O módulo não executa processo; ele consome o registro do que
/// o mecanismo controlado de execução já rodou.
/// </summary>
public sealed record ProductVerificationRecord(
    ProductEvidenceKind Kind,
    bool Succeeded,
    string Verifier,
    string Command,
    int ExitCode,
    string CommitSha,
    DateTimeOffset ObservedAt,
    string? ExecutionId = null,
    string? Artifact = null,
    string? Detail = null,

    /// <summary>
    /// Quem controlou o CONTEÚDO da verificação. Um script do manifesto do produto é
    /// <see cref="VerificationTrustLevel.ProjectControlled"/> por mais que o Poseidon o invoque.
    /// </summary>
    VerificationTrustLevel Trust = VerificationTrustLevel.PoseidonControlled);

/// <summary>
/// Compõe as fontes de evidência numa ordem que reflete a confiança: verificação executada vence
/// constatação de repositório, que vence afirmação do ator.
///
/// A afirmação do ator entra no conjunto de propósito — não para satisfazer requisito, mas para
/// aparecer no diagnóstico. Quando um card reprova porque o executor disse que buildou e ninguém
/// buildou, o motivo precisa dizer exatamente isso.
/// </summary>
public sealed class ProductEvidenceCollectorPipeline(RepositoryEvidenceCollector? repository = null)
{
    private readonly RepositoryEvidenceCollector _repository = repository ?? new RepositoryEvidenceCollector();

    public ProductDeliveryEvidence Collect(
        ProjectEffectiveProfile profile,
        IProductWorkspace workspace,
        IReadOnlyList<ProductVerificationRecord>? verifications = null,
        IReadOnlyList<ProductEvidence>? declared = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(workspace);

        var collectors = new List<string>();
        var items = new List<ProductEvidence>();

        if (declared is { Count: > 0 })
        {
            items.AddRange(declared.Select(item => item with
            {
                Provenance = item.Provenance ?? ProductEvidenceProvenanceRecord.FromActor("unknown"),
            }));
            collectors.Add("actor-declaration");
        }

        var observed = _repository.Collect(profile, workspace);
        if (observed.Count > 0)
        {
            items.AddRange(observed);
            collectors.Add("repository-scanner");
        }

        if (verifications is { Count: > 0 })
        {
            items.AddRange(verifications.Select(record => new ProductEvidence(
                record.Kind,
                record.Succeeded,
                record.Detail ?? $"{record.Command} → exit {record.ExitCode}",
                ProductEvidenceProvenanceRecord.FromVerifier(
                    record.Verifier, record.CommitSha, record.ObservedAt,
                    record.Command, record.ExitCode, record.ExecutionId, record.Artifact,
                    record.Trust))));
            collectors.Add("verification-log");
        }

        return new ProductDeliveryEvidence(workspace.CommitSha, items, collectors);
    }
}
