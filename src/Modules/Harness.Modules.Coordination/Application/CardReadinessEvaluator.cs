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
/// domínio). Regra fail-safe do loop autônomo do Chefe: só os tipos de IMPLEMENTAÇÃO são
/// auto-despacháveis — 'agent_task' (legado) e, do playbook (§5, Fase 5), 'historia', 'tarefa' e
/// 'bug' — sempre com ao menos uma instrução e sem bloqueio. 'human_gate'/'gate' e
/// 'decision'/'adr' exigem humano ou revisor; 'feature', 'spike', 'documento', 'revisao',
/// 'incidente' e 'chamado' têm condutores próprios — nenhum deles vira execução de agente
/// sozinho, então o card_type fora da lista é sempre um bloqueador.
/// </summary>
public static class CardReadinessEvaluator
{
    public const string DispatchableCardType = "agent_task";

    /// <summary>Tipos auto-despacháveis: implementação (legado + playbook Fase 5).</summary>
    public static readonly IReadOnlySet<string> DispatchableCardTypes =
        new HashSet<string>(["agent_task", "historia", "tarefa", "bug"], StringComparer.Ordinal);

    public const string CardTypeNotDispatchable = "dor.card_type.not_dispatchable";
    public const string InstructionMissing = "dor.instruction.missing";
    public const string Blocked = "dor.blocked";

    public static CardReadinessSnapshot Evaluate(CardReadinessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var blockers = new List<string>();
        if (!DispatchableCardTypes.Contains(facts.CardType))
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
