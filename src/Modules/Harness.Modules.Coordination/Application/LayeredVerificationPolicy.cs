namespace Harness.Modules.Coordination.Application;

/// <summary>As três camadas de verificação, da mais barata e objetiva à mais cara e interpretativa.</summary>
public enum VerificationLayer
{
    /// <summary>Build, testes e varredura de segredo. Determinística, roda antes de ocupar revisor.</summary>
    Deterministic = 1,

    /// <summary>Comportamento entregue × critérios de aceite declarados.</summary>
    Behavioral = 2,

    /// <summary>Intenção × objetivo da demanda, por avaliador em contexto fresco e sem escrita.</summary>
    Intent = 3
}

public enum LayerVerdict
{
    Pass = 0,
    Fail = 1,

    /// <summary>Não executada — e isso NUNCA conta como aprovação.</summary>
    NotRun = 2
}

/// <summary>Resultado de uma camada, com severidade própria.</summary>
public sealed record LayerResult(
    VerificationLayer Layer,
    LayerVerdict Verdict,
    string ReasonCode,
    string? Detail = null);

public sealed record LayeredVerificationOutcome(
    bool Approved,
    VerificationLayer? BlockedAt,
    string ReasonCode,
    IReadOnlyList<LayerResult> Results)
{
    /// <summary>Severidade do bloqueio: quanto mais baixa a camada, mais objetivo o defeito.</summary>
    public string Severity => BlockedAt switch
    {
        VerificationLayer.Deterministic => "blocker",
        VerificationLayer.Behavioral => "major",
        VerificationLayer.Intent => "major",
        _ => "none"
    };
}

/// <summary>
/// Verificação em CAMADAS (B2).
///
/// A ordem existe por economia e por honestidade. Economia: build quebrado e teste vermelho são
/// fatos objetivos que uma máquina apura em segundos — ocupar um revisor para descobri-los
/// desperdiça a atenção mais cara do sistema no defeito mais barato de achar. Honestidade: cada
/// camada responde uma pergunta diferente, e nenhuma responde a da outra.
///
/// A regra dura é a da não-compensação: <b>camada superior nunca compensa inferior</b>. Um
/// avaliador entusiasmado dizendo "a intenção está perfeita" não torna verde um teste vermelho, e
/// "os critérios de aceite foram atendidos" não vale nada se o projeto não compila. O caminho
/// inverso também não vale: passar na camada 1 não prova que o trabalho serve para o que foi pedido
/// — foi assim que o sistema aprendeu a entregar código que compila, testa e não resolve nada.
///
/// Camada não executada é bloqueio, não silêncio favorável. É a diferença entre "verificamos e está
/// bom" e "ninguém olhou".
/// </summary>
public static class LayeredVerificationPolicy
{
    public const string ReasonApproved = "verification.approved";
    public const string ReasonDeterministicFailed = "verification.deterministic_failed";
    public const string ReasonBehavioralFailed = "verification.behavioral_failed";
    public const string ReasonIntentFailed = "verification.intent_failed";
    public const string ReasonLayerNotRun = "verification.layer_not_run";

    /// <summary>Ordem obrigatória de avaliação: da mais barata para a mais cara.</summary>
    public static readonly IReadOnlyList<VerificationLayer> Order =
    [
        VerificationLayer.Deterministic,
        VerificationLayer.Behavioral,
        VerificationLayer.Intent
    ];

    /// <summary>
    /// Consolida as camadas. Para na primeira que bloqueia: seguir para a próxima gastaria o
    /// revisor caro num trabalho que já se sabe que volta.
    /// </summary>
    public static LayeredVerificationOutcome Evaluate(IReadOnlyList<LayerResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var byLayer = results
            .GroupBy(result => result.Layer)
            .ToDictionary(group => group.Key, group => group.Last());

        foreach (var layer in Order)
        {
            if (!byLayer.TryGetValue(layer, out var result) || result.Verdict == LayerVerdict.NotRun)
            {
                // Ausência não é aprovação: ninguém olhou.
                return new LayeredVerificationOutcome(
                    Approved: false, layer, ReasonLayerNotRun, results);
            }

            if (result.Verdict == LayerVerdict.Fail)
            {
                return new LayeredVerificationOutcome(
                    Approved: false, layer, FailureReason(layer), results);
            }
        }

        return new LayeredVerificationOutcome(Approved: true, null, ReasonApproved, results);
    }

    /// <summary>
    /// A camada determinística deve rodar ANTES de ocupar revisor humano ou de IA. Verdadeiro
    /// quando já é seguro chamar o revisor.
    /// </summary>
    public static bool MayOccupyReviewer(IReadOnlyList<LayerResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var deterministic = results.LastOrDefault(
            result => result.Layer == VerificationLayer.Deterministic);
        return deterministic is { Verdict: LayerVerdict.Pass };
    }

    private static string FailureReason(VerificationLayer layer) => layer switch
    {
        VerificationLayer.Deterministic => ReasonDeterministicFailed,
        VerificationLayer.Behavioral => ReasonBehavioralFailed,
        VerificationLayer.Intent => ReasonIntentFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Camada desconhecida.")
    };
}
