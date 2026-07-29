using System.Globalization;
using Harness.SharedKernel.CodeGraph;

namespace Harness.Modules.Coordination.Application;

/// <summary>
/// Risco recalculado a partir do impacto real, com a justificativa que o tornou auditável.
///
/// A justificativa não é enfeite: sem ela, "este card virou risco alto" é uma afirmação que ninguém
/// pode contestar nem reproduzir. Com ela, a decisão carrega os números que a produziram.
/// </summary>
public sealed record BlastRadiusAssessment(
    RiskTier DeclaredRisk,
    RiskTier EffectiveRisk,
    int ReviewDepth,
    bool Measured,
    string ReasonCode,
    string Justification)
{
    public bool Escalated => EffectiveRisk > DeclaredRisk;
}

/// <summary>
/// RAIO DE IMPACTO (B6) — o risco de um card não é só o que ele faz, é o que quebra se ele errar.
///
/// Um card que troca duas linhas num tipo com cento e vinte dependentes é mais perigoso que um card
/// que reescreve inteiro um arquivo que ninguém importa. Quem estima risco lendo o enunciado não
/// consegue ver isso: a diferença está no grafo, não no texto. É por isso que esta política existe —
/// ela substitui a intuição sobre "tamanho da mudança" pela contagem de dependentes.
///
/// Duas regras dão a ela o caráter de política, e não de heurística conveniente:
///
/// <b>1. O risco declarado nunca é REBAIXADO.</b> Impacto pequeno no código não desmente as outras
/// razões pelas quais alguém marcou um card como crítico (dado sensível, mudança de contrato,
/// obrigação externa). O grafo só sabe de acoplamento; achar que ele sabe do resto seria dar à
/// medição um poder que ela não tem. Então esta política só sobe.
///
/// <b>2. Não medido não é seguro.</b> Quando o índice não cobre os caminhos do card, o resultado sai
/// com <see cref="BlastRadiusAssessment.Measured"/> falso e o motivo explícito. É a mesma disciplina
/// da verificação em camadas (F14): camada que não rodou não vale como aprovação. Sem isso, o
/// intervalo em que o frontend ainda não tem índice faria todo card de tela parecer inofensivo.
/// </summary>
public static class BlastRadiusPolicy
{
    /// <summary>Dependentes a partir dos quais o acoplamento, por si só, torna o card crítico.</summary>
    public const int CriticalThreshold = 50;

    public const int HighThreshold = 20;

    public const int MediumThreshold = 5;

    public const string ReasonUnmeasured = "blast_radius.unmeasured";
    public const string ReasonEscalated = "blast_radius.escalated";
    public const string ReasonConfirmed = "blast_radius.declared_risk_kept";

    /// <summary>
    /// Recalcula o risco do card e a profundidade de revisão. A profundidade não é inventada aqui:
    /// vem da <see cref="EffortPolicy"/> aplicada ao risco EFETIVO, para não existirem duas tabelas
    /// de esforço divergindo no mesmo sistema.
    /// </summary>
    public static BlastRadiusAssessment Assess(
        RiskTier declaredRisk,
        CodeBlastRadius radius,
        WorkNature nature = WorkNature.Implementation,
        bool isSmall = false)
    {
        ArgumentNullException.ThrowIfNull(radius);
        if (!Enum.IsDefined(declaredRisk))
        {
            throw new ArgumentOutOfRangeException(nameof(declaredRisk));
        }

        if (!Enum.IsDefined(nature))
        {
            throw new ArgumentOutOfRangeException(nameof(nature));
        }

        var fromRadius = RiskFromDependents(radius.DependentCount);
        var effective = (RiskTier)Math.Max((int)declaredRisk, (int)fromRadius);
        var reviewDepth = EffortPolicy.Decide(nature, effective, isSmall).ReviewDepth;
        var reason = !radius.IsFullyMeasured
            ? ReasonUnmeasured
            : effective > declaredRisk ? ReasonEscalated : ReasonConfirmed;

        return new BlastRadiusAssessment(
            declaredRisk,
            effective,
            reviewDepth,
            radius.IsFullyMeasured,
            reason,
            Describe(declaredRisk, effective, radius, reviewDepth));
    }

    /// <summary>
    /// Faixa sugerida SÓ pelo acoplamento. Os limiares são degraus declarados, não uma curva: o valor
    /// de um limiar é ser o mesmo para todos os cards e poder ser discutido em número.
    /// </summary>
    public static RiskTier RiskFromDependents(int dependentCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dependentCount);

        return dependentCount switch
        {
            >= CriticalThreshold => RiskTier.Critical,
            >= HighThreshold => RiskTier.High,
            >= MediumThreshold => RiskTier.Medium,
            _ => RiskTier.Low
        };
    }

    private static string Describe(
        RiskTier declared,
        RiskTier effective,
        CodeBlastRadius radius,
        int reviewDepth)
    {
        var parts = new List<string>
        {
            string.Format(
                CultureInfo.InvariantCulture,
                "toca {0} nó(s) do código com {1} dependente(s) no grafo",
                radius.TouchedNodeIds.Count,
                radius.DependentCount)
        };

        if (radius.UnindexedPaths.Count > 0)
        {
            // A lista entra na justificativa (limitada) porque "não medi" sem dizer O QUE não mediu
            // é indistinguível de "medi e deu zero".
            parts.Add(string.Format(
                CultureInfo.InvariantCulture,
                "IMPACTO NÃO MEDIDO em {0} caminho(s) fora do índice: {1}",
                radius.UnindexedPaths.Count,
                string.Join(", ", radius.UnindexedPaths.Take(5))));
        }

        parts.Add(effective > declared
            ? string.Format(
                CultureInfo.InvariantCulture,
                "risco sobe de {0} para {1} pelo acoplamento",
                declared,
                effective)
            : string.Format(
                CultureInfo.InvariantCulture,
                "risco declarado {0} mantido (o acoplamento não o eleva; esta política nunca rebaixa)",
                declared));
        parts.Add(string.Format(
            CultureInfo.InvariantCulture,
            "profundidade de revisão {0}",
            reviewDepth));

        return string.Join("; ", parts) + ".";
    }
}
