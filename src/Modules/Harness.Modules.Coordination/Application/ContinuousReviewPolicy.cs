namespace Harness.Modules.Coordination.Application;

/// <summary>O que o revisor contínuo tem a dizer sobre a rodada que acabou de ler.</summary>
public enum ReviewRemarkKind
{
    /// <summary>Aparte: observação útil que não muda o rumo. Não interrompe.</summary>
    Aside = 0,

    /// <summary>Preocupação: pode dar errado adiante. Registra e segue.</summary>
    Concern = 1,

    /// <summary>Bloqueio: submeter assim produz retrabalho garantido. Interrompe.</summary>
    Block = 2
}

public sealed record ReviewRemark(ReviewRemarkKind Kind, string Message, string? Path = null);

/// <summary>Rigor do pareamento para a tentativa.</summary>
public enum ContinuousReviewMode
{
    /// <summary>Desligado: risco baixo não paga o custo de um segundo modelo lendo tudo.</summary>
    Off = 0,

    /// <summary>Ligado a pedido, mas não exigido.</summary>
    Optional = 1,

    /// <summary>Obrigatório: a tentativa não submete sem revisor pareado ativo.</summary>
    Required = 2
}

public sealed record ContinuousReviewDecision(
    bool MaySubmit,
    ReviewRemarkKind? BlockedBy,
    string ReasonCode,
    IReadOnlyList<ReviewRemark> Remarks);

/// <summary>
/// Revisor contínuo pareado (B7).
///
/// A revisão tradicional acontece no fim: o agente trabalha uma hora, submete, e só então alguém
/// descobre que a abordagem estava errada desde a terceira decisão. Todo o trabalho depois daquele
/// ponto é retrabalho, e ninguém tinha como saber.
///
/// O revisor pareado lê cada rodada enquanto ela acontece e pode interromper cedo. O ganho não é
/// achar mais defeitos — é achá-los quando corrigir ainda é barato.
///
/// Duas regras que o tornam confiável em vez de decorativo:
///
/// 1. <b>Conta distinta do executor.</b> O mesmo agente revisando a si mesmo produz concordância,
///    não revisão: ele repete o raciocínio que o levou até ali e o encontra correto.
/// 2. <b>Obrigatório em risco alto, desligado em risco baixo.</b> Pareamento é caro; gastá-lo numa
///    troca de texto o transforma em imposto, e imposto se aprende a burlar.
/// </summary>
public static class ContinuousReviewPolicy
{
    public const string ReasonMaySubmit = "review.may_submit";
    public const string ReasonBlockedByReviewer = "review.blocked_by_reviewer";
    public const string ReasonReviewerMissing = "review.reviewer_missing";
    public const string ReasonReviewerIsTheExecutor = "review.reviewer_is_the_executor";

    /// <summary>Rigor exigido pelo risco do card.</summary>
    public static ContinuousReviewMode ModeFor(RiskTier risk)
    {
        if (!Enum.IsDefined(risk))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }

        return risk switch
        {
            RiskTier.Critical or RiskTier.High => ContinuousReviewMode.Required,
            RiskTier.Medium => ContinuousReviewMode.Optional,
            _ => ContinuousReviewMode.Off
        };
    }

    /// <summary>
    /// Decide se a rodada pode ser submetida. <paramref name="reviewerAlias"/> nulo significa que
    /// não houve revisor pareado nesta tentativa.
    /// </summary>
    public static ContinuousReviewDecision Evaluate(
        RiskTier risk,
        string executorAlias,
        string? reviewerAlias,
        IReadOnlyList<ReviewRemark> remarks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executorAlias);
        ArgumentNullException.ThrowIfNull(remarks);

        var mode = ModeFor(risk);

        // Revisor que é o próprio executor não é revisor: ele repete o raciocínio que o trouxe até
        // aqui e o encontra correto. Isso vale mesmo quando o pareamento é apenas opcional.
        if (!string.IsNullOrWhiteSpace(reviewerAlias) &&
            string.Equals(reviewerAlias, executorAlias, StringComparison.OrdinalIgnoreCase))
        {
            return new ContinuousReviewDecision(
                MaySubmit: false, null, ReasonReviewerIsTheExecutor, remarks);
        }

        if (mode == ContinuousReviewMode.Required && string.IsNullOrWhiteSpace(reviewerAlias))
        {
            return new ContinuousReviewDecision(
                MaySubmit: false, null, ReasonReviewerMissing, remarks);
        }

        var block = remarks.FirstOrDefault(remark => remark.Kind == ReviewRemarkKind.Block);
        if (block is not null)
        {
            // Submeter com bloqueio aberto garante retrabalho: o revisor já disse que volta.
            return new ContinuousReviewDecision(
                MaySubmit: false, ReviewRemarkKind.Block, ReasonBlockedByReviewer, remarks);
        }

        // Aparte e preocupação são registrados e NÃO interrompem: um revisor que barra tudo é
        // indistinguível de um portão fechado, e o executor deixa de ler o que ele diz.
        return new ContinuousReviewDecision(MaySubmit: true, null, ReasonMaySubmit, remarks);
    }
}
