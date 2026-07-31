namespace Harness.Host.WorkBoard;

/// <summary>
/// Arestas em que a materialização pode ser interrompida por uma queda real. Existem para que o
/// teste possa MATAR o fluxo exatamente ali e provar a convergência — não há nenhuma configuração,
/// flag ou variável de ambiente que ative uma interrupção em produção: a única implementação
/// registrada pelo Host é <see cref="NullPlanMaterializationFaultInjector"/>, que não faz nada.
/// </summary>
public enum PlanMaterializationStage
{
    /// <summary>Evento durável já commitado; o consumidor ainda não começou.</summary>
    BeforeConsume,

    /// <summary>O primeiro card do plano acabou de ser criado.</summary>
    AfterFirstCard,

    /// <summary>Um card intermediário acabou de ser criado.</summary>
    BetweenCards,

    /// <summary>Todos os cards previstos existem; a validação ainda não rodou.</summary>
    AfterCards,

    /// <summary>Cardinalidade e dependências validadas; o marker ainda não foi gravado.</summary>
    AfterDependencies,

    /// <summary>Último instante antes de carimbar o plano como materializado.</summary>
    BeforeMarker,

    /// <summary>Marker gravado; o compromisso ainda não foi concluído.</summary>
    AfterMarker,

    /// <summary>Compromisso concluído; o evento de outbox ainda não foi confirmado.</summary>
    BeforeAcknowledgement,
}

public interface IPlanMaterializationFaultInjector
{
    Task SignalAsync(PlanMaterializationStage stage, CancellationToken cancellationToken = default);
}

/// <summary>A implementação de produção: nenhuma aresta é interrompida.</summary>
public sealed class NullPlanMaterializationFaultInjector : IPlanMaterializationFaultInjector
{
    public static readonly NullPlanMaterializationFaultInjector Instance = new();

    public Task SignalAsync(
        PlanMaterializationStage stage, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
