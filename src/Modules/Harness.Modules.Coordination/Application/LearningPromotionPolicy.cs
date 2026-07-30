namespace Harness.Modules.Coordination.Application;

/// <summary>Estágio de um candidato a aprendizado.</summary>
public enum LearningStage
{
    /// <summary>Observado uma vez. Ainda é anedota.</summary>
    Candidate = 0,

    /// <summary>Repetiu-se o bastante para ser levado a sério, mas ainda não vale como regra.</summary>
    Corroborated = 1,

    /// <summary>Pronto para a decisão humana. Só o dono promove.</summary>
    AwaitingApproval = 2,

    /// <summary>Promovido a skill procedural, carregável por escopo.</summary>
    Promoted = 3,

    /// <summary>Recusado pelo humano. Não volta a pedir aprovação pela mesma evidência.</summary>
    Rejected = 4
}

/// <summary>Uma evidência que sustenta (ou contradiz) o candidato.</summary>
public sealed record LearningEvidence(string TaskId, bool Supports, DateTimeOffset OccurredAt);

/// <summary>Candidato a aprendizado, com a confiança acumulada pela evidência.</summary>
public sealed record LearningCandidate(
    string Id,
    string Scope,
    string Lesson,
    LearningStage Stage = LearningStage.Candidate,
    int Supporting = 0,
    int Contradicting = 0,
    string? ApprovedBy = null,
    DateTimeOffset? ApprovedAt = null)
{
    /// <summary>
    /// Confiança pela evidência observada. Contradição pesa: uma lição que falha às vezes é pior que
    /// nenhuma lição, porque ela é aplicada com convicção justamente onde não vale.
    /// </summary>
    public double Confidence =>
        Supporting + Contradicting == 0
            ? 0d
            : (double)Supporting / (Supporting + Contradicting);

    public bool IsLoadable => Stage == LearningStage.Promoted;
}

/// <summary>
/// Ciclo do aprendizado (B10): candidato → confiança acumulada por evidência → promoção COM
/// aprovação humana.
///
/// A tentação aqui é a promoção automática: se a lição se confirmou dez vezes, por que pedir
/// aprovação? Porque o sistema estaria escrevendo as próprias regras a partir do próprio
/// comportamento. Uma correlação observada dez vezes num repositório pode ser uma verdade do
/// domínio — ou o eco de um viés que o próprio sistema introduziu e passou a confirmar. Distinguir
/// as duas coisas exige alguém de fora do laço, e esse alguém é o dono.
///
/// Por isso a aprovação humana é obrigatória e permanece obrigatória: nenhum acúmulo de evidência,
/// nenhuma confiança alta, nenhum limiar promove sozinho. O que a evidência faz é decidir o que
/// merece a ATENÇÃO do dono — não substituí-la.
///
/// Recusa é definitiva para aquela evidência: voltar a pedir aprovação do mesmo item cansa o dono e
/// transforma o pedido em ruído que ele aprende a ignorar. Só evidência nova reabre.
/// </summary>
public static class LearningPromotionPolicy
{
    /// <summary>Evidências favoráveis para o candidato merecer a atenção do dono.</summary>
    public const int CorroborationThreshold = 3;

    /// <summary>Confiança mínima para pedir aprovação. Lição intermitente não é lição.</summary>
    public const double MinimumConfidence = 0.80d;

    public const string ReasonStillAnecdote = "learning.still_anecdote";
    public const string ReasonCorroborated = "learning.corroborated";
    public const string ReasonAwaitingHuman = "learning.awaiting_human_approval";
    public const string ReasonPromoted = "learning.promoted";
    public const string ReasonRejected = "learning.rejected";
    public const string ReasonContradicted = "learning.contradicted";
    public const string ReasonApprovalRequired = "learning.approval_required";

    /// <summary>Registra uma evidência e reavalia o estágio.</summary>
    public static LearningCandidate Observe(LearningCandidate candidate, LearningEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(evidence);

        // Promovido ou recusado já saiu do ciclo de acumulação: nova evidência não o reabre
        // sozinha, senão o dono voltaria a decidir o mesmo item indefinidamente.
        if (candidate.Stage is LearningStage.Promoted or LearningStage.Rejected)
        {
            return candidate;
        }

        var updated = evidence.Supports
            ? candidate with { Supporting = candidate.Supporting + 1 }
            : candidate with { Contradicting = candidate.Contradicting + 1 };

        return updated with { Stage = Classify(updated) };
    }

    /// <summary>
    /// Exige aprovador humano identificado antes de qualquer promoção. Existe como ponto de
    /// verificação independente do agregado: quem promove por outro caminho (endpoint, importação,
    /// migração) passa por aqui e recebe a mesma recusa. O invariante é "não existe promoção
    /// silenciosa" — e um invariante que só vale num caminho não é invariante.
    /// </summary>
    public static void RequireHumanApproval(string? approvedBy)
    {
        if (string.IsNullOrWhiteSpace(approvedBy))
        {
            throw new InvalidOperationException(
                "Promoção a skill exige aprovação humana identificada — não existe promoção automática.");
        }
    }

    /// <summary>
    /// Promove — e SÓ com aprovação humana identificada. Sem aprovador, isto lança: não existe
    /// caminho de promoção silenciosa, nem por conveniência de teste.
    /// </summary>
    public static LearningCandidate Promote(
        LearningCandidate candidate,
        string approvedBy,
        DateTimeOffset approvedAt)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(approvedBy))
        {
            throw new InvalidOperationException(
                "Promoção a skill exige aprovação humana identificada — não existe promoção automática.");
        }

        if (candidate.Stage != LearningStage.AwaitingApproval)
        {
            throw new InvalidOperationException(
                $"Somente candidato em '{LearningStage.AwaitingApproval}' pode ser promovido; " +
                $"este está em '{candidate.Stage}'.");
        }

        return candidate with
        {
            Stage = LearningStage.Promoted,
            ApprovedBy = approvedBy.Trim(),
            ApprovedAt = approvedAt
        };
    }

    public static LearningCandidate Reject(LearningCandidate candidate, string rejectedBy)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectedBy);
        return candidate with { Stage = LearningStage.Rejected, ApprovedBy = rejectedBy.Trim() };
    }

    /// <summary>Skills carregáveis para um escopo: só as promovidas, e só as daquele escopo.</summary>
    public static IReadOnlyList<LearningCandidate> LoadableFor(
        string scope,
        IReadOnlyList<LearningCandidate> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(candidate => candidate.IsLoadable)
            .Where(candidate => string.Equals(candidate.Scope, scope, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static LearningStage Classify(LearningCandidate candidate) =>
        candidate.Supporting >= CorroborationThreshold && candidate.Confidence >= MinimumConfidence
            ? LearningStage.AwaitingApproval
            : candidate.Supporting >= CorroborationThreshold
                ? LearningStage.Corroborated
                : LearningStage.Candidate;
}
