namespace Harness.Modules.Workflows.Product;

/// <summary>Um item do plano: o que verificar, e por quê ele é (ou não é) exigido.</summary>
public sealed record ProductVerificationStep(
    ProductEvidenceKind Kind,
    bool Required,
    string Rationale);

/// <summary>
/// O plano determinístico de verificação de um projeto, derivado do perfil efetivo.
///
/// Existe separado da execução para ser AUDITÁVEL: dá para responder "por que o Poseidon rodou o
/// build do frontend neste projeto e não naquele?" sem ler log de processo. E é derivado, nunca
/// declarado — o modelo não escolhe o que será verificado.
/// </summary>
public sealed record ProductVerificationPlan(
    ProductModality Modality,
    string BaselineVersion,
    IReadOnlyList<ProductVerificationStep> Steps)
{
    public IReadOnlyList<ProductEvidenceKind> Required =>
        [.. Steps.Where(step => step.Required).Select(step => step.Kind)];

    public IReadOnlyList<ProductEvidenceKind> NotApplicable =>
        [.. Steps.Where(step => !step.Required).Select(step => step.Kind)];

    public string Summary() => string.Join(
        ' ',
        Steps.OrderBy(step => step.Kind)
            .Select(step => $"{step.Kind.ToString().ToLowerInvariant()}={(step.Required ? "required" : "n/a")}"));

    /// <summary>
    /// Deriva o plano do perfil. Toda decisão carrega o motivo, porque um plano que não explica
    /// por que exige o que exige é indistinguível de uma lista arbitrária.
    /// </summary>
    public static ProductVerificationPlan From(ProjectEffectiveProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var required = ProductDeliveryRequirements.For(profile).ToHashSet();
        var steps = new List<ProductVerificationStep>();

        foreach (var kind in Enum.GetValues<ProductEvidenceKind>())
        {
            steps.Add(new ProductVerificationStep(
                kind,
                required.Contains(kind),
                required.Contains(kind)
                    ? RequiredBecause(kind, profile)
                    : $"A modalidade {profile.Modality} não exige {kind}."));
        }

        return new ProductVerificationPlan(profile.Modality, profile.BaselineVersion, steps);
    }

    private static string RequiredBecause(ProductEvidenceKind kind, ProjectEffectiveProfile profile) => kind switch
    {
        ProductEvidenceKind.BackendPresent or ProductEvidenceKind.BackendBuild =>
            $"O perfil declara backend em {profile.Backend.Runtime ?? "runtime não fixado"}.",
        ProductEvidenceKind.FrontendPresent or ProductEvidenceKind.FrontendBuild =>
            $"O perfil exige interface em {profile.Frontend.Framework ?? "framework não fixado"}.",
        ProductEvidenceKind.FrontendBackendIntegration =>
            "O produto tem interface E API: uma tela que nunca chama o backend não é o produto.",
        ProductEvidenceKind.ApiPresent =>
            $"O perfil declara API {profile.Api.Protocol ?? "HTTP"}.",
        ProductEvidenceKind.OpenApiGenerated =>
            "O perfil marca o contrato OpenAPI como obrigatório.",
        ProductEvidenceKind.DatabaseMigrationValidated =>
            $"O perfil declara persistência em {profile.Data.Database ?? "banco não fixado"}.",
        ProductEvidenceKind.PersistenceVerified =>
            "Ter migration não prova que a aplicação persiste: são fatos diferentes.",
        ProductEvidenceKind.E2EJourneyPassed =>
            "Produto operado por pessoa precisa de jornada exercitada ponta a ponta.",
        ProductEvidenceKind.AutomatedTestsPassed =>
            "Toda entrega precisa de suíte executada.",
        ProductEvidenceKind.RunbookPresent =>
            "Produto que só quem escreveu consegue operar não foi entregue.",
        _ => "Exigido pelo perfil efetivo.",
    };
}
