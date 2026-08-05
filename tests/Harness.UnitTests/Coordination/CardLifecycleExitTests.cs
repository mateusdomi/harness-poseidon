using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Coordination.Domain.Work;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// Onda 0.8 — F-12 provado: NENHUM estado do ciclo de vida do card fica sem aresta de saída.
///
/// O cenário original: falta de revisor → adiar → escalar → e o card escalado sem caminho de
/// volta era um beco. O mapa fechado abaixo é a prova de que todo estado não-terminal tem saída
/// conhecida — e este teste quebra no dia em que alguém adicionar um estado sem pensar na saída.
/// </summary>
public sealed class CardLifecycleExitTests
{
    private static readonly WorkTaskState[] Terminais = [WorkTaskState.Done, WorkTaskState.Cancelled];

    private static bool TemSaida(WorkTaskState from) =>
        Enum.GetValues<WorkTaskState>().Distinct().Any(to =>
            Enum.GetValues<WorkTaskTransitionEvent>().Any(evento =>
                WorkTaskTransitionPolicy.IsAllowed(from, to, evento)));

    [Fact]
    public void TodoEstadoNaoTerminalTemPeloMenosUmaArestaDeSaida()
    {
        var semSaida = Enum.GetValues<WorkTaskState>().Distinct()
            .Where(state => !Terminais.Contains(state))
            .Where(state => !TemSaida(state))
            .ToArray();

        Assert.True(
            semSaida.Length == 0,
            $"Estados sem aresta de saída: {string.Join(", ", semSaida)}. " +
            "Um card que entra num deles nunca mais sai — é o F-12 de volta.");
    }

    /// <summary>A cadeia exata do F-12, aresta por aresta: escalou, replaneja e volta ao trabalho.</summary>
    [Fact]
    public void ACadeiaDoF12TemVoltaProvada()
    {
        Assert.True(WorkTaskTransitionPolicy.IsAllowed(
            WorkTaskState.Review, WorkTaskState.Escalated, WorkTaskTransitionEvent.ReviewLimitExceeded));
        Assert.True(WorkTaskTransitionPolicy.IsAllowed(
            WorkTaskState.Escalated, WorkTaskState.Ready, WorkTaskTransitionEvent.Replanned));
        Assert.True(WorkTaskTransitionPolicy.IsAllowed(
            WorkTaskState.Blocked, WorkTaskState.Ready, WorkTaskTransitionEvent.Unblocked));
    }

    [Fact]
    public void EstadosTerminaisNaoTemSaida()
    {
        Assert.All(Terminais, state => Assert.False(TemSaida(state), $"{state} deveria ser terminal."));
    }
}
