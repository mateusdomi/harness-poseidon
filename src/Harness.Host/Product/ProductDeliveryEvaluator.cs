using Harness.Modules.Workflows.Product;
using Harness.Persistence.Abstractions.Product;
using Harness.SharedKernel.Time;

namespace Harness.Host.Product;

/// <summary>
/// Motivos que este avaliador registra no portão. São códigos fechados porque viram achado
/// impeditivo e precisam ser explicáveis ao dono sem leitura de log.
/// </summary>
public static class ProductDeliveryFailures
{
    /// <summary>A Arquitetura terminou sem materializar o perfil efetivo do projeto.</summary>
    public const string ProfileMissing = "product:effective_profile_missing";

    /// <summary>O projeto tem perfil, mas a entrega não satisfaz a Definition of Done do produto.</summary>
    public const string DeliveryIncomplete = "product:definition_of_done_failed";

    /// <summary>
    /// Projeto anterior ao baseline de produto: o portão do produto não incide, e o fato fica
    /// registrado. Compatibilidade legada precisa ter nome, não ser silêncio.
    /// </summary>
    public const string LegacyProjectExempt = "product:legacy_project_not_governed";
}

/// <summary>
/// Liga a execução ao portão do produto.
///
/// Responde a duas perguntas que o driver de fases não sabia fazer: a Arquitetura produziu o perfil
/// efetivo? e a entrega satisfaz o que esse perfil exige? A segunda usa evidência CONSTATADA da
/// árvore entregue, não o que o executor escreveu.
///
/// <b>Compatibilidade explícita.</b> Um projeto sem perfil persistido é anterior a este baseline: o
/// portão do produto não incide sobre ele, e isso vira um registro nomeado
/// (<see cref="ProductDeliveryFailures.LegacyProjectExempt"/>), nunca um `return Ready` escondido.
/// A janela fecha sozinha porque a Fase 3 passa a exigir o perfil de todo projeto novo.
/// </summary>
public sealed class ProductDeliveryEvaluator(
    IProjectEffectiveProfileStore profiles,
    IClock clock,
    ILogger<ProductDeliveryEvaluator>? logger = null)
{
    private readonly ProductEvidenceCollectorPipeline _pipeline = new();

    /// <summary>Ordem da fase de Arquitetura no playbook padrão.</summary>
    public const int ArchitecturePhaseOrder = 3;

    /// <summary>A partir da fase de Desenvolvimento, a entrega passa a ser cobrada.</summary>
    public const int DevelopmentPhaseOrder = 5;

    public sealed record Outcome(
        ProductDeliveryVerdict? Verdict,
        string? Failure,
        ProjectEffectiveProfileRecord? Profile);

    public async Task<Outcome> EvaluateAsync(
        string tenantId,
        string projectId,
        string? repositoryRoot,
        string commitSha,
        int phaseOrder,
        CancellationToken cancellationToken)
    {
        var record = await profiles.GetCurrentAsync(tenantId, projectId, cancellationToken);

        // Fase 3: sem perfil ao fim da Arquitetura, o gate não fecha. O gate desta fase sempre
        // cobrou "aderência ao constraint profile"; sem o artefato, ele cobrava o inexistente.
        if (phaseOrder >= ArchitecturePhaseOrder && record is null)
        {
            return new Outcome(null, ProductDeliveryFailures.ProfileMissing, null);
        }

        if (phaseOrder < DevelopmentPhaseOrder)
        {
            return new Outcome(null, null, record);
        }

        if (record is null)
        {
            LogLegacyProjectExempt(logger, projectId);
            return new Outcome(null, ProductDeliveryFailures.LegacyProjectExempt, null);
        }

        var profile = ProjectEffectiveProfile.FromJson(record.ProfileJson);
        if (profile is null)
        {
            // Perfil ilegível é fail-closed: não sabemos o que o projeto decidiu, então não temos
            // como afirmar que a entrega atende à decisão.
            return new Outcome(null, ProductDeliveryFailures.ProfileMissing, record);
        }

        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
        {
            // Sem árvore para inspecionar não há como constatar nada — e "não deu para verificar"
            // nunca vira "está tudo certo".
            return new Outcome(
                ProductDeliveryGate.Evaluate(profile, [], commitSha),
                ProductDeliveryFailures.DeliveryIncomplete,
                record);
        }

        var workspace = new FileSystemProductWorkspace(repositoryRoot, commitSha);
        var evidence = _pipeline.Collect(profile, workspace);
        var verdict = ProductDeliveryGate.Evaluate(profile, evidence.Items, commitSha);
        LogProductVerdict(logger, projectId, record.Version, verdict.Summary(), evidence.Summary());

        return new Outcome(
            verdict,
            verdict.Satisfied ? null : ProductDeliveryFailures.DeliveryIncomplete,
            record);
    }

    /// <summary>
    /// Resolve e PERSISTE o perfil efetivo do projeto. Idempotente por conteúdo: resolver duas
    /// vezes a mesma coisa não cria versão nova, e mudar uma decisão cria.
    /// </summary>
    public async Task<ProjectEffectiveProfileSaveResult> MaterializeAsync(
        string tenantId,
        string projectId,
        string demandText,
        IReadOnlyList<ProfileDirective> directives,
        IReadOnlyList<string> activeAdrs,
        string resolvedBy,
        CancellationToken cancellationToken)
    {
        var profile = EffectiveProfileResolver.Resolve(
            new EffectiveProfileInputs(projectId, demandText, directives, activeAdrs));

        return await profiles.SaveAsync(
            new ProjectEffectiveProfileSaveCommand(
                tenantId,
                projectId,
                profile.Fingerprint(),
                profile.BaselineVersion,
                profile.Modality.ToString(),
                profile.ToJson(),
                clock.UtcNow,
                resolvedBy),
            cancellationToken);
    }

    private static void LogLegacyProjectExempt(ILogger? logger, string projectId)
    {
        if (logger is not null)
        {
            LegacyExempt(logger, projectId, null);
        }
    }

    private static void LogProductVerdict(
        ILogger? logger, string projectId, int version, string verdict, string evidence)
    {
        if (logger is not null)
        {
            ProductVerdict(logger, projectId, version, verdict, evidence, null);
        }
    }

    private static readonly Action<ILogger, string, Exception?> LegacyExempt =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LegacyExempt)),
            "Projeto {ProjectId} não tem perfil efetivo persistido: o portão do produto NÃO incide " +
            "sobre ele (compatibilidade com projetos anteriores ao baseline de produto).");

    private static readonly Action<ILogger, string, int, string, string, Exception?> ProductVerdict =
        LoggerMessage.Define<string, int, string, string>(
            LogLevel.Information,
            new EventId(2, nameof(ProductVerdict)),
            "Portão do produto para {ProjectId} (perfil v{ProfileVersion}): {Verdict}; evidências: {Evidence}");
}
