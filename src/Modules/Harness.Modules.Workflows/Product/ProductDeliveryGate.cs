namespace Harness.Modules.Workflows.Product;

/// <summary>
/// O que precisa ser COMPROVADO para uma entrega ser considerada pronta. Conjunto fechado e
/// extensível por acréscimo: cada valor corresponde a um fato observável da execução, nunca a um
/// documento que afirma o fato.
/// </summary>
public enum ProductEvidenceKind
{
    /// <summary>O backend existe e compila.</summary>
    BackendBuild,

    /// <summary>Existe projeto de frontend no entregável.</summary>
    FrontendPresent,

    /// <summary>O frontend compila.</summary>
    FrontendBuild,

    /// <summary>O frontend consome o backend de verdade, não um mock.</summary>
    FrontendBackendIntegration,

    /// <summary>Existe superfície HTTP.</summary>
    ApiPresent,

    /// <summary>A especificação OpenAPI foi gerada sem erro.</summary>
    OpenApiGenerated,

    /// <summary>Existem migrations e o schema é reproduzível.</summary>
    DatabaseMigrationValidated,

    /// <summary>Dados persistem de verdade.</summary>
    PersistenceVerified,

    /// <summary>Testes automatizados relevantes passaram.</summary>
    AutomatedTestsPassed,

    /// <summary>A jornada principal foi exercitada ponta a ponta.</summary>
    E2EJourneyPassed,

    /// <summary>Existem instruções de execução (README/runbook).</summary>
    RunbookPresent,

    /// <summary>Nenhuma falha crítica de segurança conhecida.</summary>
    SecurityScanPassed,
}

/// <summary>
/// Um fato observado da execução. <see cref="Satisfied"/> falso e evidência AUSENTE são coisas
/// diferentes e ambas reprovam — a diferença aparece no motivo, para o diagnóstico não mentir.
/// </summary>
public sealed record ProductEvidence(ProductEvidenceKind Kind, bool Satisfied, string? Detail = null);

/// <summary>Por que uma exigência não foi satisfeita.</summary>
public enum ProductEvidenceGap
{
    /// <summary>A evidência não foi produzida nem reportada.</summary>
    Missing,

    /// <summary>A evidência foi produzida e reprovou.</summary>
    Failed,
}

public sealed record ProductEvidenceFinding(
    ProductEvidenceKind Kind,
    ProductEvidenceGap Gap,
    string Reason);

public sealed record ProductDeliveryVerdict(
    bool Satisfied,
    ProductModality Modality,
    IReadOnlyList<ProductEvidenceKind> Required,
    IReadOnlyList<ProductEvidenceFinding> Findings)
{
    /// <summary>Resumo curto e estável para log, recibo e mensagem de reprovação.</summary>
    public string Summary() => Satisfied
        ? $"product_dod_satisfied:{Modality.ToString().ToLowerInvariant()}"
        : "product_dod_failed:" + string.Join(
            ',',
            Findings.Select(finding =>
                $"{finding.Kind.ToString().ToLowerInvariant()}:{finding.Gap.ToString().ToLowerInvariant()}"));
}

/// <summary>
/// Deriva do perfil efetivo QUAIS evidências a entrega precisa ter. A modalidade manda: um produto
/// web exige interface; uma API declarada explicitamente não exige — mas continua exigindo o
/// contrato, a persistência e os testes.
/// </summary>
public static class ProductDeliveryRequirements
{
    public static IReadOnlyList<ProductEvidenceKind> For(ProjectEffectiveProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var required = new List<ProductEvidenceKind>();

        if (profile.Backend.Required)
        {
            required.Add(ProductEvidenceKind.BackendBuild);
        }

        if (profile.Frontend.Required)
        {
            // Três exigências distintas de propósito: existir, compilar e conversar de verdade com
            // o backend. Uma tela que só renderiza mock atende às duas primeiras e não entrega
            // produto nenhum.
            required.Add(ProductEvidenceKind.FrontendPresent);
            required.Add(ProductEvidenceKind.FrontendBuild);
            if (profile.Api.Required)
            {
                required.Add(ProductEvidenceKind.FrontendBackendIntegration);
            }
        }

        if (profile.Api.Required)
        {
            required.Add(ProductEvidenceKind.ApiPresent);
        }

        if (profile.Api.OpenApiRequired)
        {
            required.Add(ProductEvidenceKind.OpenApiGenerated);
        }

        if (profile.Data.Required)
        {
            required.Add(ProductEvidenceKind.DatabaseMigrationValidated);
            required.Add(ProductEvidenceKind.PersistenceVerified);
        }

        required.Add(ProductEvidenceKind.AutomatedTestsPassed);

        // Jornada ponta a ponta só faz sentido onde existe jornada de pessoa.
        if (profile.Frontend.Required)
        {
            required.Add(ProductEvidenceKind.E2EJourneyPassed);
        }

        required.Add(ProductEvidenceKind.RunbookPresent);

        return required;
    }
}

/// <summary>
/// O gate que separa "código gerado" de "sistema entregue" — PURO e Default-FAIL.
///
/// A regra que ele existe para impor: <b>o documento não é a evidência</b>. Ter
/// `definition-of-done.md` no repositório não prova que a Definition of Done foi cumprida; ter
/// `frontend-standards.md` não prova que existe frontend. O primeiro é norma, o segundo precisa
/// ser fato observado da execução.
///
/// Foi exatamente esse vão que deixou passar um `GET /emprestimos` devolvendo lista vazia como se
/// fosse um sistema de empréstimos.
/// </summary>
public static class ProductDeliveryGate
{
    public static ProductDeliveryVerdict Evaluate(
        ProjectEffectiveProfile profile,
        IReadOnlyList<ProductEvidence>? observed)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var required = ProductDeliveryRequirements.For(profile);
        var byKind = (observed ?? [])
            .GroupBy(evidence => evidence.Kind)
            // Uma evidência reportada duas vezes com vereditos diferentes não pode virar "passou":
            // vale a mais severa.
            .ToDictionary(group => group.Key, group => group.All(evidence => evidence.Satisfied));

        var findings = new List<ProductEvidenceFinding>();
        foreach (var kind in required)
        {
            if (!byKind.TryGetValue(kind, out var satisfied))
            {
                findings.Add(new ProductEvidenceFinding(
                    kind,
                    ProductEvidenceGap.Missing,
                    $"A entrega exige {kind} pela modalidade {profile.Modality}, e nenhuma evidência foi registrada."));
                continue;
            }

            if (!satisfied)
            {
                findings.Add(new ProductEvidenceFinding(
                    kind,
                    ProductEvidenceGap.Failed,
                    $"A evidência {kind} foi registrada e reprovou."));
            }
        }

        // Modalidade não resolvida NUNCA passa: significa que ninguém decidiu que produto é este,
        // e aprovar o indefinido é aprovar sem critério.
        if (profile.Modality == ProductModality.Unspecified)
        {
            findings.Add(new ProductEvidenceFinding(
                ProductEvidenceKind.BackendBuild,
                ProductEvidenceGap.Missing,
                "A modalidade do produto não foi resolvida no perfil efetivo; o que significa 'pronto' é indefinido."));
        }

        return new ProductDeliveryVerdict(findings.Count == 0, profile.Modality, required, findings);
    }
}
