using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// A fronteira que a Fase 5 existe para endurecer.
///
/// Na Fase 4, um produto podia declarar <c>"test:e2e": "node -e \"process.exit(0)\""</c>, o Poseidon
/// invocava esse script pelo nome convencionado, recebia exit zero e registrava evidência
/// `Verified`. O comando era do Poseidon; o CONTEÚDO era de quem estava sendo avaliado.
///
/// Nome de script confiável não torna o conteúdo do script uma verificação confiável.
/// </summary>
public sealed class ScriptFraudResistanceTests
{
    private const string Commit = "cafe0000cafe0000cafe0000cafe0000cafe0000";

    [Fact]
    public void ScriptDoProdutoQueSoRetornaZeroNaoAprovaAJornadaE2E()
    {
        var verdict = Avaliar(Completo(fraudarE2E: true));

        Assert.False(verdict.Satisfied);
        var finding = Assert.Single(verdict.Findings);
        Assert.Equal(ProductEvidenceKind.E2EJourneyPassed, finding.Kind);
        Assert.Equal(ProductEvidenceGap.Untrusted, finding.Gap);
        Assert.Contains(
            "script definido pelo próprio produto", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptDoProdutoNaoAprovaOContratoOpenApi()
    {
        var verdict = Avaliar(Completo(fraudarOpenApi: true));

        Assert.False(verdict.Satisfied);
        Assert.Contains(verdict.Findings, finding =>
            finding.Kind == ProductEvidenceKind.OpenApiGenerated &&
            finding.Gap == ProductEvidenceGap.Untrusted);
    }

    [Fact]
    public void ScriptDoProdutoNaoAprovaPersistenciaNemIntegracao()
    {
        var verdict = Avaliar(Completo(fraudarPersistencia: true, fraudarIntegracao: true));

        Assert.False(verdict.Satisfied);
        Assert.Equal(2, verdict.Findings.Count);
        Assert.All(verdict.Findings, finding => Assert.Equal(ProductEvidenceGap.Untrusted, finding.Gap));
    }

    [Fact]
    public void BuildETesteDoBackendContinuamValendoPorqueNaoHaScriptDoProdutoNoMeio()
    {
        // `dotnet build` e `dotnet test` são comandos do Poseidon sobre o código real; não há
        // script do manifesto decidindo o que eles fazem. Endurecer aqui seria endurecer o que já
        // é confiável e travar toda entrega legítima.
        var verdict = Avaliar(Completo());

        Assert.True(verdict.Satisfied);
    }

    [Fact]
    public void EvidenciaSemNivelDeConfiancaDeclaradoValeComoControladaPeloAtor()
    {
        var evidence = new ProductEvidence(ProductEvidenceKind.E2EJourneyPassed, true);

        Assert.Equal(VerificationTrustLevel.ActorControlled, evidence.Trust);
    }

    /// <summary>
    /// O plano com verificadores NATIVOS registrados para os quatro requisitos que um script vazio
    /// poderia fingir. É o registro do verificador nativo que eleva a barra — sem ele, o script do
    /// produto continua valendo como caminho de compatibilidade (§17).
    /// </summary>
    private static ProductDeliveryVerdict Avaliar(ProductEvidence[] evidencias) =>
        ProductDeliveryGate.Evaluate(
            Web(), evidencias, Commit,
            ProductVerificationPlan.From(Web(), new Dictionary<ProductEvidenceKind, string>
            {
                [ProductEvidenceKind.E2EJourneyPassed] = "playwright",
                [ProductEvidenceKind.OpenApiGenerated] = "openapi-native",
                [ProductEvidenceKind.PersistenceVerified] = "persistence-native",
                [ProductEvidenceKind.FrontendBackendIntegration] = "integration-native",
            }));

    /// <summary>
    /// Conjunto completo e legítimo; cada parâmetro rebaixa UM requisito para o nível do script do
    /// produto, que é exatamente o que a fraude produz.
    /// </summary>
    private static ProductEvidence[] Completo(
        bool fraudarE2E = false,
        bool fraudarOpenApi = false,
        bool fraudarPersistencia = false,
        bool fraudarIntegracao = false) =>
    [
        .. Enum.GetValues<ProductEvidenceKind>().Select(kind => new ProductEvidence(
            kind, true, null,
            Observavel(kind)
                ? ProductEvidenceProvenanceRecord.FromRepository(Commit, DateTimeOffset.UnixEpoch)
                : ProductEvidenceProvenanceRecord.FromVerifier(
                    Fraudado(kind, fraudarE2E, fraudarOpenApi, fraudarPersistencia, fraudarIntegracao)
                        ? $"npm-script:{kind}"
                        : "poseidon-verifier",
                    Commit, DateTimeOffset.UnixEpoch, "verify", 0,
                    trust: Fraudado(kind, fraudarE2E, fraudarOpenApi, fraudarPersistencia, fraudarIntegracao)
                        ? VerificationTrustLevel.ProjectControlled
                        : VerificationTrustLevel.PoseidonControlled))),
    ];

    private static bool Observavel(ProductEvidenceKind kind) =>
        kind is ProductEvidenceKind.BackendPresent or ProductEvidenceKind.FrontendPresent
            or ProductEvidenceKind.ApiPresent or ProductEvidenceKind.DatabaseMigrationValidated
            or ProductEvidenceKind.RunbookPresent;

    private static bool Fraudado(
        ProductEvidenceKind kind, bool e2e, bool openApi, bool persistencia, bool integracao) =>
        (e2e && kind == ProductEvidenceKind.E2EJourneyPassed) ||
        (openApi && kind == ProductEvidenceKind.OpenApiGenerated) ||
        (persistencia && kind == ProductEvidenceKind.PersistenceVerified) ||
        (integracao && kind == ProductEvidenceKind.FrontendBackendIntegration);

    private static ProjectEffectiveProfile Web() => EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs("projeto", "Crie um sistema simples de empréstimos.", []));
}
