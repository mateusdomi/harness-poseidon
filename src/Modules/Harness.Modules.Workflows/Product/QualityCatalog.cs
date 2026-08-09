namespace Harness.Modules.Workflows.Product;

/// <summary>
/// Superfície operacional à qual uma verificação de qualidade se aplica.
/// </summary>
public enum QualitySurface
{
    Frontend,
    Backend,
    Api,
    Database,
    Security,
    Upload,
    Auth,
    Responsive,
    Accessibility,
    Integration,
    Deployment,
    FinalProduct,
}

public enum QualityExecutionMoment
{
    ObjectiveSelfAudit,
    ObjectiveProof,
    FinalProductProof,
}

public enum QualityProofType
{
    Deterministic,
    StaticInspection,
    Runtime,
    Browser,
    Database,
    LlmJudgement,
    HumanAcceptance,
}

public enum QualityAutomation
{
    Automated,
    SemiAutomated,
    Manual,
}

public enum QualityObligation
{
    Mandatory,
    Conditional,
}

/// <summary>
/// Família derivada do checklist de auto-auditoria. Uma família representa N perguntas do checklist,
/// em vez de injetar as perguntas uma a uma em cada card.
/// </summary>
public sealed record QualityCheck(
    string Id,
    string Category,
    IReadOnlySet<QualitySurface> Surfaces,
    QualityExecutionMoment Moment,
    QualityProofType ProofType,
    QualityAutomation Automation,
    QualityObligation Obligation,
    string EvidenceProvider,
    int SourceItemStart,
    int SourceItemEnd)
{
    public int SourceItemCount => SourceItemEnd - SourceItemStart + 1;

    public bool AppliesTo(IReadOnlySet<QualitySurface> surfaces) =>
        Surfaces.Overlaps(surfaces);
}

public sealed record QualityCatalogSummary(
    int TotalSourceItems,
    int Deterministic,
    int Browser,
    int Runtime,
    int Database,
    int LlmJudgement,
    int HumanAcceptance);

/// <summary>
/// Catálogo derivado do arquivo externo <c>checklist-auto-auditoria-ia.md</c>.
///
/// O checklist possui 244 perguntas. Este catálogo mantém a contabilidade completa por intervalos
/// de origem, mas seleciona famílias aplicáveis ao escopo real do objetivo. Isso preserva a régua
/// sem transformar cada card em um prompt de 244 itens.
/// </summary>
public static class QualityCatalog
{
    public const int SourceChecklistItemCount = 244;

    public static IReadOnlyList<QualityCheck> Checks { get; } =
    [
        Check("browser-console-network", "Console, Network e rotas", [QualitySurface.Frontend, QualitySurface.Integration],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Browser, QualityAutomation.Automated,
            QualityObligation.Conditional, "platform-playwright-console-network", 1, 7),
        Check("react-structure", "React — comportamento e estrutura", [QualitySurface.Frontend],
            QualityExecutionMoment.ObjectiveSelfAudit, QualityProofType.StaticInspection, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "eslint-react-hooks/static-inspection", 8, 17),
        Check("forms-validation", "Formulários e validação", [QualitySurface.Frontend, QualitySurface.Api],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Browser, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "browser-form-flow", 18, 30),
        Check("ui-states", "Estados de UI", [QualitySurface.Frontend],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Browser, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "browser-state-coverage", 31, 36),
        Check("responsive-layout", "Responsividade e layout", [QualitySurface.Frontend, QualitySurface.Responsive],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Browser, QualityAutomation.Automated,
            QualityObligation.Conditional, "playwright-viewports", 37, 44),
        Check("accessibility", "Acessibilidade", [QualitySurface.Frontend, QualitySurface.Accessibility],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Browser, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "axe/playwright-keyboard", 45, 52),
        Check("openapi-contract", "Contrato da API / Swagger / OpenAPI", [QualitySurface.Api, QualitySurface.Integration],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Runtime, QualityAutomation.Automated,
            QualityObligation.Conditional, "openapi-native-verifier", 53, 58),
        Check("dotnet-api-behaviour", "API .NET — comportamento e qualidade", [QualitySurface.Backend, QualitySurface.Api],
            QualityExecutionMoment.ObjectiveSelfAudit, QualityProofType.Deterministic, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "dotnet-test/static-inspection", 59, 70),
        Check("security", "Segurança", [QualitySurface.Security, QualitySurface.Auth, QualitySurface.Backend, QualitySurface.Frontend],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Deterministic, QualityAutomation.Automated,
            QualityObligation.Mandatory, "secret-scan/security-baseline-verifier", 71, 82),
        Check("database", "Banco de dados", [QualitySurface.Database],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Database, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "migration/persistence-native-verifier", 83, 94),
        Check("front-back-integration", "Integração front ↔ back", [QualitySurface.Frontend, QualitySurface.Api, QualitySurface.Integration],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Browser, QualityAutomation.Automated,
            QualityObligation.Conditional, "platform-playwright-network", 95, 102),
        Check("edge-robustness", "Casos de borda e robustez", [QualitySurface.Frontend, QualitySurface.Backend, QualitySurface.Api],
            QualityExecutionMoment.ObjectiveSelfAudit, QualityProofType.Runtime, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "tests-and-browser-negative-paths", 103, 109),
        Check("performance", "Performance", [QualitySurface.Frontend, QualitySurface.Backend, QualitySurface.Api],
            QualityExecutionMoment.FinalProductProof, QualityProofType.Runtime, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "bundle-analysis/load-smoke", 110, 115),
        Check("automated-tests", "Testes", [QualitySurface.Backend, QualitySurface.Frontend, QualitySurface.Api, QualitySurface.Database],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Deterministic, QualityAutomation.Automated,
            QualityObligation.Mandatory, "dotnet-test/npm-test", 116, 120),
        Check("observability", "Logging e observabilidade", [QualitySurface.Backend, QualitySurface.Api, QualitySurface.Deployment],
            QualityExecutionMoment.ObjectiveSelfAudit, QualityProofType.Runtime, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "health-check/log-inspection", 121, 124),
        Check("build-config-deploy", "Configuração, build e deploy", [QualitySurface.Backend, QualitySurface.Frontend, QualitySurface.Deployment],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Deterministic, QualityAutomation.Automated,
            QualityObligation.Mandatory, "build/lockfile/vulnerability-scan", 125, 131),
        Check("code-quality", "Qualidade de código e versionamento", [QualitySurface.Backend, QualitySurface.Frontend, QualitySurface.Security],
            QualityExecutionMoment.ObjectiveSelfAudit, QualityProofType.Deterministic, QualityAutomation.Automated,
            QualityObligation.Mandatory, "lint/formatter/todo-secret-scan", 132, 137),
        Check("documentation", "Documentação e entrega", [QualitySurface.FinalProduct, QualitySurface.Deployment],
            QualityExecutionMoment.FinalProductProof, QualityProofType.StaticInspection, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "readme/runbook-inspection", 138, 141),
        Check("real-execution", "Provas de execução real", [QualitySurface.FinalProduct, QualitySurface.Integration],
            QualityExecutionMoment.FinalProductProof, QualityProofType.Browser, QualityAutomation.Automated,
            QualityObligation.Mandatory, "platform-e2e-runner", 142, 150),
        Check("real-user-flow", "Fluxo ponta a ponta do usuário real", [QualitySurface.Frontend, QualitySurface.Auth, QualitySurface.Integration, QualitySurface.FinalProduct],
            QualityExecutionMoment.FinalProductProof, QualityProofType.Browser, QualityAutomation.Automated,
            QualityObligation.Conditional, "platform-playwright-product-suite", 151, 160),
        Check("connected-product", "Front e back realmente conectados", [QualitySurface.Frontend, QualitySurface.Api, QualitySurface.Integration, QualitySurface.Auth],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Browser, QualityAutomation.Automated,
            QualityObligation.Conditional, "platform-playwright-network", 161, 169),
        Check("agent-integration", "Integração entre agentes / partes", [QualitySurface.Integration, QualitySurface.FinalProduct],
            QualityExecutionMoment.FinalProductProof, QualityProofType.LlmJudgement, QualityAutomation.Manual,
            QualityObligation.Conditional, "product-validator", 170, 180),
        Check("mock-placeholder-cleanup", "Mocks, placeholders e dados falsos", [QualitySurface.Frontend, QualitySurface.Backend, QualitySurface.Security],
            QualityExecutionMoment.ObjectiveSelfAudit, QualityProofType.StaticInspection, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "static-inspection/browser-smoke", 181, 189),
        Check("branding", "Identidade, favicon e branding", [QualitySurface.Frontend, QualitySurface.FinalProduct],
            QualityExecutionMoment.FinalProductProof, QualityProofType.Browser, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "browser-metadata-screenshot", 190, 197),
        Check("interactive-actions", "Botões, links e ações reais", [QualitySurface.Frontend],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Browser, QualityAutomation.Automated,
            QualityObligation.Conditional, "platform-playwright-actions", 198, 206),
        Check("ui-resilience", "Travamentos, loading eterno e recuperação", [QualitySurface.Frontend, QualitySurface.Upload],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Browser, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "browser-negative-paths", 207, 214),
        Check("upload", "Upload real", [QualitySurface.Upload, QualitySurface.Frontend, QualitySurface.Api, QualitySurface.Database],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Browser, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "browser-upload-fixture", 215, 224),
        Check("cross-feature-regression", "Regressão entre funcionalidades", [QualitySurface.Integration, QualitySurface.FinalProduct],
            QualityExecutionMoment.FinalProductProof, QualityProofType.Browser, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "product-e2e-regression-suite", 225, 232),
        Check("contract-sync", "Contrato único e tipos sincronizados", [QualitySurface.Api, QualitySurface.Frontend, QualitySurface.Integration],
            QualityExecutionMoment.ObjectiveProof, QualityProofType.Deterministic, QualityAutomation.SemiAutomated,
            QualityObligation.Conditional, "openapi-contract-drift", 233, 238),
        Check("done-means-proven", "Definition of Done comportamental", [QualitySurface.FinalProduct, QualitySurface.Integration],
            QualityExecutionMoment.FinalProductProof, QualityProofType.HumanAcceptance, QualityAutomation.Manual,
            QualityObligation.Mandatory, "human-acceptance/product-validator", 239, 244),
    ];

    public static IReadOnlyList<QualityCheck> Select(QualitySelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var surfaces = SurfacesFor(selection);
        return [.. Checks
            .Where(check => check.Moment <= selection.Moment && check.AppliesTo(surfaces))
            .Where(check => check.Obligation == QualityObligation.Mandatory ||
                check.Surfaces.Overlaps(selection.ExplicitSurfaces.Count > 0 ? selection.ExplicitSurfaces : surfaces))
            .OrderBy(check => check.Moment)
            .ThenBy(check => check.Id)];
    }

    public static QualityCatalogSummary Summary()
    {
        var total = Checks.Sum(check => check.SourceItemCount);
        return new QualityCatalogSummary(
            total,
            Count(QualityProofType.Deterministic) + Count(QualityProofType.StaticInspection),
            Count(QualityProofType.Browser),
            Count(QualityProofType.Runtime),
            Count(QualityProofType.Database),
            Count(QualityProofType.LlmJudgement),
            Count(QualityProofType.HumanAcceptance));

        static int Count(QualityProofType type) =>
            Checks.Where(check => check.ProofType == type).Sum(check => check.SourceItemCount);
    }

    public static IReadOnlySet<QualitySurface> SurfacesFor(QualitySelection selection)
    {
        var surfaces = new HashSet<QualitySurface>(selection.ExplicitSurfaces);

        if (selection.Profile.Backend.Required)
        {
            surfaces.Add(QualitySurface.Backend);
        }

        if (selection.Profile.Api.Required)
        {
            surfaces.Add(QualitySurface.Api);
        }

        if (selection.Profile.Data.Required)
        {
            surfaces.Add(QualitySurface.Database);
        }

        if (selection.Profile.Frontend.Required)
        {
            surfaces.Add(QualitySurface.Frontend);
            surfaces.Add(QualitySurface.Responsive);
            surfaces.Add(QualitySurface.Accessibility);
        }

        if (selection.Profile.Frontend.Required && selection.Profile.Api.Required)
        {
            surfaces.Add(QualitySurface.Integration);
        }

        surfaces.Add(QualitySurface.Security);
        return surfaces;
    }

    private static QualityCheck Check(
        string id,
        string category,
        QualitySurface[] surfaces,
        QualityExecutionMoment moment,
        QualityProofType proofType,
        QualityAutomation automation,
        QualityObligation obligation,
        string provider,
        int start,
        int end) =>
        new(id, category, surfaces.ToHashSet(), moment, proofType, automation, obligation, provider, start, end);
}

public sealed record QualitySelection(
    ProjectEffectiveProfile Profile,
    QualityExecutionMoment Moment,
    IReadOnlySet<QualitySurface> ExplicitSurfaces);

public sealed record SelfAuditEvidence(
    string CheckId,
    bool Passed,
    ProductEvidenceProvenance Provenance,
    string Provider,
    string? Detail = null);

public sealed record SelfAuditFinding(string CheckId, string Reason);

public sealed record SelfAuditVerdict(bool Satisfied, IReadOnlyList<SelfAuditFinding> Findings);

/// <summary>
/// Gate da auto-auditoria do executor. Texto do ator é registrado, mas não satisfaz verificação
/// automatizável quando o catálogo exige prova executável/observável.
/// </summary>
public static class QualitySelfAuditGate
{
    public static SelfAuditVerdict Evaluate(
        IReadOnlyList<QualityCheck> selectedChecks,
        IReadOnlyList<SelfAuditEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(selectedChecks);
        ArgumentNullException.ThrowIfNull(evidence);

        var byCheck = evidence
            .GroupBy(item => item.CheckId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var findings = new List<SelfAuditFinding>();

        foreach (var check in selectedChecks.Where(check => check.Obligation == QualityObligation.Mandatory ||
            check.Automation != QualityAutomation.Manual))
        {
            if (!byCheck.TryGetValue(check.Id, out var candidates))
            {
                findings.Add(new SelfAuditFinding(
                    check.Id,
                    $"Sem evidência para {check.Category}; provider esperado: {check.EvidenceProvider}."));
                continue;
            }

            if (candidates.Any(item => !item.Passed))
            {
                findings.Add(new SelfAuditFinding(check.Id, $"{check.Category} foi executado e reprovou."));
                continue;
            }

            var strongest = candidates.OrderByDescending(item => item.Provenance).First();
            var minimum = check.Automation == QualityAutomation.Automated
                ? ProductEvidenceProvenance.Verified
                : ProductEvidenceProvenance.Observed;
            if (strongest.Provenance < minimum)
            {
                findings.Add(new SelfAuditFinding(
                    check.Id,
                    $"{check.Category} exige evidência {minimum}; texto do executor não substitui prova automatizável."));
            }
        }

        return new SelfAuditVerdict(findings.Count == 0, findings);
    }
}
