namespace Harness.Modules.Workflows.Product;

/// <summary>
/// Um item do plano: o que verificar, com que força de prova, e por quê.
///
/// O NÍVEL DE PROVA mora aqui, não no portão. É o que permite ao gate continuar genérico — ele
/// compara a evidência com o mínimo que o plano exige, sem nunca perguntar se o projeto é React ou
/// Angular, .NET ou Node.
/// </summary>
public sealed record ProductVerificationStep(
    ProductEvidenceKind Kind,
    bool Required,
    string Rationale,
    ProductEvidenceProvenance MinimumProvenance = ProductEvidenceProvenance.Verified,
    VerificationTrustLevel MinimumTrust = VerificationTrustLevel.ProjectControlled,
    string? PreferredVerifier = null);

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
    ///
    /// <paramref name="nativeVerifiers"/> são os tipos para os quais existe verificador cujo
    /// CONTEÚDO o Poseidon controla. A regra do §17: enquanto não existe verificador nativo para um
    /// requisito, o script do produto continua valendo como caminho de compatibilidade; assim que
    /// passa a existir, o script deixa de bastar. Registrar um verificador nativo eleva a barra
    /// daquele requisito sozinho — sem tocar no portão.
    /// </summary>
    public static ProductVerificationPlan From(
        ProjectEffectiveProfile profile,
        IReadOnlyDictionary<ProductEvidenceKind, string>? nativeVerifiers = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var required = ProductDeliveryRequirements.For(profile).ToHashSet();
        var native = nativeVerifiers ?? EmptyNative;
        var steps = new List<ProductVerificationStep>();

        foreach (var kind in Enum.GetValues<ProductEvidenceKind>())
        {
            var isRequired = required.Contains(kind);
            steps.Add(new ProductVerificationStep(
                kind,
                isRequired,
                isRequired
                    ? RequiredBecause(kind, profile)
                    : $"A modalidade {profile.Modality} não exige {kind}.",
                MinimumProvenanceFor(kind),
                native.ContainsKey(kind)
                    ? VerificationTrustLevel.PoseidonControlled
                    : VerificationTrustLevel.ProjectControlled,
                native.GetValueOrDefault(kind)));
        }

        return new ProductVerificationPlan(profile.Modality, profile.BaselineVersion, steps);
    }

    private static readonly Dictionary<ProductEvidenceKind, string> EmptyNative = [];

    /// <summary>
    /// Existência e forma se constatam olhando; funcionamento exige execução. Este limiar não
    /// depende de haver verificador nativo — depende da natureza do fato.
    /// </summary>
    private static ProductEvidenceProvenance MinimumProvenanceFor(ProductEvidenceKind kind) => kind switch
    {
        ProductEvidenceKind.BackendPresent or
        ProductEvidenceKind.FrontendPresent or
        ProductEvidenceKind.ApiPresent or
        ProductEvidenceKind.DatabaseMigrationValidated or
        ProductEvidenceKind.RunbookPresent => ProductEvidenceProvenance.Observed,
        _ => ProductEvidenceProvenance.Verified,
    };

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
