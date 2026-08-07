namespace Harness.Modules.Workflows.Product;

/// <summary>
/// Por que um passo do plano está no estado em que está. O §16 existe por causa da confusão que
/// este tipo desfaz: <b>"não existe verificador implementado" não é "requisito não aplicável"</b>.
/// </summary>
public enum ProductVerificationDisposition
{
    /// <summary>O perfil exige e existe verificador nativo: prova controlada pelo Poseidon.</summary>
    RequiredNative,

    /// <summary>
    /// O perfil exige e só existe caminho controlado pelo produto. Vale como compatibilidade
    /// enquanto nenhum verificador nativo cobre o tipo — e deixa de valer no dia em que um cobrir.
    /// </summary>
    RequiredProjectControlled,

    /// <summary>
    /// O perfil exige, a plataforma NÃO sabe verificar, e ninguém está dispensado. É o estado que o
    /// §16 obriga a nomear: o buraco é da plataforma, e o requisito continua reprovando.
    /// </summary>
    RequiredUnsupported,

    /// <summary>A modalidade não exige. Só aqui o requisito realmente não incide.</summary>
    NotApplicable,
}

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
    string? PreferredVerifier = null,
    ProductVerificationDisposition Disposition = ProductVerificationDisposition.NotApplicable);

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
        [.. Steps.Where(step => step.Disposition == ProductVerificationDisposition.NotApplicable)
            .Select(step => step.Kind)];

    /// <summary>
    /// Requisitos que o perfil exige e que a plataforma NÃO sabe verificar. É o buraco declarado —
    /// e a razão de existir: enquanto esta lista não estiver vazia, dizer que a plataforma está
    /// pronta para um Golden Run daquele perfil é falso.
    /// </summary>
    public IReadOnlyList<ProductEvidenceKind> Unsupported =>
        [.. Steps.Where(step => step.Disposition == ProductVerificationDisposition.RequiredUnsupported)
            .Select(step => step.Kind)];

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
        IReadOnlyDictionary<ProductEvidenceKind, string>? nativeVerifiers = null,
        IReadOnlyDictionary<ProductEvidenceKind, string>? projectControlledVerifiers = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var required = ProductDeliveryRequirements.For(profile).ToHashSet();
        var native = nativeVerifiers ?? EmptyNative;
        var scripted = projectControlledVerifiers ?? EmptyNative;
        var steps = new List<ProductVerificationStep>();

        foreach (var kind in Enum.GetValues<ProductEvidenceKind>())
        {
            var isRequired = required.Contains(kind);
            var hasNative = native.ContainsKey(kind);
            var hasAnyProvider = hasNative || scripted.ContainsKey(kind) || ObservedByScanner(kind);

            var disposition = !isRequired
                ? ProductVerificationDisposition.NotApplicable
                : hasNative
                    ? ProductVerificationDisposition.RequiredNative
                    : hasAnyProvider
                        ? ProductVerificationDisposition.RequiredProjectControlled
                        : ProductVerificationDisposition.RequiredUnsupported;

            steps.Add(new ProductVerificationStep(
                kind,
                isRequired,
                disposition switch
                {
                    ProductVerificationDisposition.NotApplicable =>
                        $"A modalidade {profile.Modality} não exige {kind}.",
                    ProductVerificationDisposition.RequiredUnsupported =>
                        $"{RequiredBecause(kind, profile)} NENHUM verificador sabe produzir esta " +
                        "evidência: o requisito continua valendo e a plataforma não o alcança.",
                    _ => RequiredBecause(kind, profile),
                },
                MinimumProvenanceFor(kind),
                hasNative
                    ? VerificationTrustLevel.PoseidonControlled
                    : VerificationTrustLevel.ProjectControlled,
                native.GetValueOrDefault(kind) ?? scripted.GetValueOrDefault(kind),
                disposition));
        }

        return new ProductVerificationPlan(profile.Modality, profile.BaselineVersion, steps);
    }

    /// <summary>
    /// Tipos que o inspetor de repositório constata sem executar nada. Têm produtor por construção —
    /// é o que impede que "existe backend" apareça como buraco da plataforma.
    /// </summary>
    private static bool ObservedByScanner(ProductEvidenceKind kind) => kind is
        ProductEvidenceKind.BackendPresent or
        ProductEvidenceKind.FrontendPresent or
        ProductEvidenceKind.ApiPresent or
        ProductEvidenceKind.DatabaseMigrationValidated or
        ProductEvidenceKind.DataAccessDeclared or
        ProductEvidenceKind.RunbookPresent;

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
        ProductEvidenceKind.DataAccessDeclared or
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
        ProductEvidenceKind.DataAccessDeclared =>
            $"O perfil fixa {profile.Data.Database}: o driver precisa estar declarado na entrega — " +
            "migration sem driver é persistência de fachada (caso Indicadores, avaliação TrensRJ).",
        ProductEvidenceKind.E2EJourneyPassed =>
            "Produto operado por pessoa precisa de jornada exercitada ponta a ponta.",
        ProductEvidenceKind.AutomatedTestsPassed =>
            "Toda entrega precisa de suíte executada.",
        ProductEvidenceKind.RunbookPresent =>
            "Produto que só quem escreveu consegue operar não foi entregue.",
        ProductEvidenceKind.SecurityScanPassed =>
            "Segredo em código e dependência com vulnerabilidade conhecida reprovam qualquer entrega, " +
            "em qualquer modalidade.",
        _ => "Exigido pelo perfil efetivo.",
    };
}
