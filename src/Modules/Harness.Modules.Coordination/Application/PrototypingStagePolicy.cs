namespace Harness.Modules.Coordination.Application;

/// <summary>Estado da etapa opcional de Prototipação numa variante de workflow.</summary>
public enum PrototypingStageState
{
    /// <summary>A variante não tem frontend: a etapa não existe para esta demanda.</summary>
    NotApplicable = 0,

    /// <summary>Ativa e pendente: falta identidade visual ou protótipo aprovado.</summary>
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
/// Etapa OPCIONAL de Prototipação, entre Planejamento e Desenvolvimento (D11).
///
/// Duas decisões de desenho que valem explicar:
///
/// <b>Opcional e data-driven, não uma fase 4.5.</b> A homologação propôs numerar a etapa como "4.5".
/// Foi rejeitado: fases são dados no catálogo de workflow, e renumerar por estética quebraria quadro,
/// portões e documentos que já referenciam os números. Inserir uma etapa nas variantes que têm
/// frontend é natural; renumerar as existentes é dano gratuito.
///
/// <b>O portão aceita HERANÇA, não só aprovação.</b> Um portão que exigisse aprovação explícita
/// mesmo quando a organização já tem marca e design system transformaria continuidade em burocracia:
/// o dono seria chamado para aprovar o que ele já aprovou uma vez. Herdar é o caminho normal; ser
/// perguntado é a exceção de quem ainda não tem identidade definida.
///
/// O que a etapa NUNCA faz é se auto-satisfazer em silêncio: quando herda, ela diz de onde herdou.
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
            // Sem frontend a etapa não existe. Marcá-la como "satisfeita" mentiria sobre um trabalho
            // que nunca precisou ser feito.
            return new PrototypingStageVerdict(
                PrototypingStageState.NotApplicable,
                BlocksAdvance: false,
                ReasonNotApplicable,
                "Este projeto não tem telas, então não há etapa de protótipo.");
        }

        if (prototypeApproved)
        {
            return new PrototypingStageVerdict(
                PrototypingStageState.SatisfiedByApproval,
                BlocksAdvance: false,
                ReasonApproved,
                "Você aprovou o protótipo, então a equipe pode começar o desenvolvimento.");
        }

        // Herança é o caminho normal de quem já tem identidade — e ela é DECLARADA, nunca silenciosa.
        if (assets.HasBrand && assets.HasDesignSystem)
        {
            return new PrototypingStageVerdict(
                PrototypingStageState.SatisfiedByInheritance,
                BlocksAdvance: false,
                ReasonInherited,
                "Aproveitei a identidade visual e os padrões de tela que já estavam cadastrados, " +
                "então não precisei te pedir nada novo.");
        }

        return assets.HasBrand
            ? new PrototypingStageVerdict(
                PrototypingStageState.Pending,
                BlocksAdvance: true,
                ReasonPendingApproval,
                "Preparei o protótipo das telas e preciso do seu aval antes de desenvolver.")
            : new PrototypingStageVerdict(
                PrototypingStageState.Pending,
                BlocksAdvance: true,
                ReasonPendingIdentity,
                "Ainda não tenho a identidade visual do projeto — me diga o logo e as cores, " +
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
