namespace Harness.Modules.Coordination.Application;

/// <summary>Limites da amostragem de cauda dos turnos.</summary>
public sealed record TurnTailSamplingOptions(
    TimeSpan? SlowThreshold = null,
    int BaselineKeepEvery = 10)
{
    /// <summary>Acima disto o turno é cauda por duração e é sempre retido.</summary>
    public TimeSpan Slow => SlowThreshold ?? TimeSpan.FromSeconds(30);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BaselineKeepEvery);
        if (Slow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SlowThreshold),
                "O limiar de lentidão precisa ser positivo.");
        }
    }
}

/// <summary>Fatos de um turno JÁ ENCERRADO — a amostragem de cauda só decide depois do fim.</summary>
public sealed record TurnTailSamplingFacts(
    string TraceKey,
    TimeSpan Duration,
    string Outcome,
    bool GuardBlocked = false);

public sealed record TurnTailSamplingDecision(bool Keep, string ReasonCode);

/// <summary>
/// Amostragem de CAUDA dos turnos do Chefe.
///
/// Amostragem de cabeça (decidir no início, por sorteio) descarta exatamente o que se precisa ver:
/// o turno raro que falhou, o que demorou demais, o que a guarda de laço interrompeu. Esses são
/// eventos de cauda — por definição, os que uma taxa uniforme quase nunca captura.
///
/// A decisão aqui é tomada com o turno encerrado e a duração conhecida: toda cauda é retida
/// integralmente, e o corpo normal da distribuição entra por uma fração determinística — derivada
/// do identificador do trace, não de sorteio, para que o mesmo turno decida sempre igual e o teste
/// seja reproduzível.
/// </summary>
public static class TurnTailSamplingPolicy
{
    public const string ReasonGuardBlocked = "tail.guard_blocked";
    public const string ReasonNotCompleted = "tail.not_completed";
    public const string ReasonSlow = "tail.slow";
    public const string ReasonBaseline = "tail.baseline";
    public const string ReasonDropped = "tail.dropped";

    /// <summary>Único desfecho considerado corpo da distribuição; todo o resto é cauda.</summary>
    public const string CompletedOutcome = "completed";

    public static TurnTailSamplingDecision Decide(
        TurnTailSamplingFacts facts,
        TurnTailSamplingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(facts.TraceKey);

        var effective = options ?? new TurnTailSamplingOptions();
        effective.Validate();

        if (facts.GuardBlocked)
        {
            return new TurnTailSamplingDecision(Keep: true, ReasonGuardBlocked);
        }

        if (!string.Equals(facts.Outcome, CompletedOutcome, StringComparison.OrdinalIgnoreCase))
        {
            return new TurnTailSamplingDecision(Keep: true, ReasonNotCompleted);
        }

        if (facts.Duration >= effective.Slow)
        {
            return new TurnTailSamplingDecision(Keep: true, ReasonSlow);
        }

        return StableBucket(facts.TraceKey, effective.BaselineKeepEvery) == 0
            ? new TurnTailSamplingDecision(Keep: true, ReasonBaseline)
            : new TurnTailSamplingDecision(Keep: false, ReasonDropped);
    }

    /// <summary>
    /// Balde determinístico e estável entre processos — FNV-1a sobre a chave do trace. Estabilidade
    /// importa: um mesmo trace precisa cair sempre no mesmo balde, aqui e em qualquer réplica.
    /// </summary>
    private static int StableBucket(string traceKey, int modulus)
    {
        unchecked
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;
            var hash = offsetBasis;
            foreach (var character in traceKey)
            {
                hash ^= character;
                hash *= prime;
            }

            return (int)(hash % (uint)modulus);
        }
    }
}
