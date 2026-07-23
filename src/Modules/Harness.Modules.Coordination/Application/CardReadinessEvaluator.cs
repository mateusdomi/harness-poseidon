namespace Harness.Modules.Coordination.Application;

/// <summary>
/// Fatos puros de um card já coletados do board — sem IO. <see cref="CardType"/> é o tipo do card
/// ('feature','agent_task','human_gate','spike','decision'); <see cref="HasInstruction"/> indica se
/// existe ao menos uma versão de instrução; <see cref="IsBlocked"/> reflete board_state 'blocked' ou
/// um blocked_reason presente.
/// </summary>
public sealed record CardReadinessFacts(string CardType, bool HasInstruction, bool IsBlocked);

/// <summary>
/// Veredito de prontidão-para-despacho (Definition of Ready) de um card. Somente leitura.
/// <see cref="IsDispatchable"/> só é verdadeiro quando NENHUM bloqueador tipado se aplica.
/// </summary>
public sealed record CardReadinessSnapshot(bool IsDispatchable, IReadOnlyList<string> Blockers);

/// <summary>
/// Avaliador PURO e determinístico da Definition of Ready (DoR) de um card. Espelha a forma do
/// <c>ReadinessEvaluator</c> (função pura, códigos de bloqueador tipados, sem IO, sem autoridade de
/// domínio). Regra fail-safe do loop autônomo do Chefe: um card só é auto-despachável quando é um
/// 'agent_task', tem ao menos uma instrução e não está bloqueado. 'human_gate' e 'decision' exigem
/// um humano; 'feature' e 'spike' são portadores de escopo/investigação — nenhum deles deve virar
/// execução de agente sozinho, então o card_type errado é sempre um bloqueador.
/// </summary>
public static class CardReadinessEvaluator
{
    public const string DispatchableCardType = "agent_task";

    public const string CardTypeNotDispatchable = "dor.card_type.not_dispatchable";
    public const string InstructionMissing = "dor.instruction.missing";
    public const string Blocked = "dor.blocked";

    public static CardReadinessSnapshot Evaluate(CardReadinessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var blockers = new List<string>();
        if (!string.Equals(facts.CardType, DispatchableCardType, StringComparison.Ordinal))
        {
            blockers.Add(CardTypeNotDispatchable);
        }

        if (!facts.HasInstruction)
        {
            blockers.Add(InstructionMissing);
        }

        if (facts.IsBlocked)
        {
            blockers.Add(Blocked);
        }

        return new CardReadinessSnapshot(blockers.Count == 0, blockers);
    }
}
