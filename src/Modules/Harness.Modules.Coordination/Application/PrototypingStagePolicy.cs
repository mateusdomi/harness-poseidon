namespace Harness.Modules.Coordination.Application;

/// <summary>Estado de prontidão das referências visuais/protótipos numa variante com interface.</summary>
public enum PrototypingStageState
{
    /// <summary>A variante não tem frontend: referências visuais não são necessárias.</summary>
    NotApplicable = 0,

    /// <summary>Pendente: falta identidade visual, referência visual ou protótipo aprovado.</summary>
    Pending = 1,

    /// <summary>Auto-satisfeita: marca e design system já existiam e foram herdados.</summary>
    SatisfiedByInheritance = 2,

    /// <summary>Satisfeita por aprovação do dono no protótipo produzido.</summary>
    SatisfiedByApproval = 3
}

public sealed record PrototypingStageVerdict(
    PrototypingStageState State,
    bool BlocksAdvance,
    string ReasonCode,
    string BusinessMessage);

/// <summary>
/// Política de prontidão de referências visuais/protótipos.
///
/// Duas decisões de desenho que valem explicar:
///
/// <b>Não é lifecycle V3.</b> Protótipo, frontend fornecido e referência visual são artefatos
/// do projeto. Eles não criam fase adicional entre Entendimento, Desenvolvimento, Validação e
/// Aceite Humano. O nome público do contrato permanece por compatibilidade de API.
///
/// <b>A política aceita HERANÇA, não só aprovação.</b> Uma política que exigisse aprovação explícita
/// mesmo quando a organização já tem marca e design system transformaria continuidade em burocracia:
/// o dono seria chamado para aprovar o que ele já aprovou uma vez. Herdar é o caminho normal; ser
/// perguntado é a exceção de quem ainda não tem identidade definida.
///
/// O que a política NUNCA faz é se auto-satisfazer em silêncio: quando herda, ela diz de onde herdou.
/// </summary>
public static class PrototypingStagePolicy
{
    public const string ReasonNotApplicable = "prototyping.stage_not_applicable";
    public const string ReasonInherited = "prototyping.gate_satisfied_by_inheritance";
    public const string ReasonApproved = "prototyping.gate_satisfied_by_approval";
    public const string ReasonPendingIdentity = "prototyping.gate_pending_identity";
    public const string ReasonPendingApproval = "prototyping.gate_pending_prototype_approval";

    /// <summary>
    /// Avalia o portão "identidade visual e protótipo aprovados OU herdados".
    /// <paramref name="hasFrontend"/> vem da variante de workflow, não de suposição.
    /// </summary>
    public static PrototypingStageVerdict Evaluate(
        bool hasFrontend,
        OrganizationDesignAssets assets,
        bool prototypeApproved)
    {
        ArgumentNullException.ThrowIfNull(assets);

        if (!hasFrontend)
        {
            return new PrototypingStageVerdict(
                PrototypingStageState.NotApplicable,
                BlocksAdvance: false,
                ReasonNotApplicable,
                "Este projeto não tem interface visual, então referências de tela/protótipo não são necessárias.");
        }

        if (prototypeApproved)
        {
            return new PrototypingStageVerdict(
                PrototypingStageState.SatisfiedByApproval,
                BlocksAdvance: false,
                ReasonApproved,
                "Você aprovou o protótipo/referência visual, então o desenvolvimento pode usar essa base.");
        }

        // Herança é o caminho normal de quem já tem identidade — e ela é DECLARADA, nunca silenciosa.
        if (assets.HasBrand && assets.HasDesignSystem)
        {
            return new PrototypingStageVerdict(
                PrototypingStageState.SatisfiedByInheritance,
                BlocksAdvance: false,
                ReasonInherited,
                "Aproveitei a identidade visual e os padrões de tela que já estavam cadastrados, " +
                "então não preciso te pedir nada novo sobre interface.");
        }

        return assets.HasBrand
            ? new PrototypingStageVerdict(
                PrototypingStageState.Pending,
                BlocksAdvance: true,
                ReasonPendingApproval,
                "Existe referência visual/protótipo pendente de confirmação antes de usar como base de desenvolvimento.")
            : new PrototypingStageVerdict(
                PrototypingStageState.Pending,
                BlocksAdvance: true,
                ReasonPendingIdentity,
                "Ainda não tenho a identidade visual do projeto — informe logo e cores, " +
                "ou me envie as telas que você já tem.");
    }

    /// <summary>
    /// Insere a etapa na sequência de etapas da variante, entre Planejamento e Desenvolvimento.
    /// Nenhuma etapa existente é renumerada: a nova entra na posição, e a ordem faz o resto.
    /// </summary>
    public static IReadOnlyList<string> InsertStage(
        IReadOnlyList<string> stages,
        string planningStage = "Planejamento",
        string developmentStage = "Desenvolvimento",
        string prototypingStage = "Prototipação")
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentException.ThrowIfNullOrWhiteSpace(planningStage);
        ArgumentException.ThrowIfNullOrWhiteSpace(developmentStage);
        ArgumentException.ThrowIfNullOrWhiteSpace(prototypingStage);

        if (stages.Contains(prototypingStage, StringComparer.Ordinal))
        {
            return stages;
        }

        var developmentIndex = stages
            .Select((stage, index) => (stage, index))
            .Where(pair => string.Equals(pair.stage, developmentStage, StringComparison.Ordinal))
            .Select(pair => pair.index)
            .DefaultIfEmpty(-1)
            .First();

        // Sem a etapa de Desenvolvimento não há entre-onde inserir: devolver a sequência intacta é
        // mais honesto que anexar ao fim e fingir que a ordem foi respeitada.
        if (developmentIndex < 0)
        {
            return stages;
        }

        var result = stages.ToList();
        result.Insert(developmentIndex, prototypingStage);
        return result;
    }
}
