using Harness.Host.Execution;

namespace Harness.UnitTests.Execution;

/// <summary>
/// O padrão de isolamento — e por que ele não pode ser `Disabled`.
///
/// Numa instalação com Docker rodando e tudo pronto, o modo `Disabled` fazia a atestação resolver
/// `unverified:none`, toda execução ser recusada com `sandbox_required`, e o card voltar para a
/// fila. No piloto real isso rendeu VINTE E UMA tentativas do mesmo card, canceladas em
/// milissegundos, sem que nada dissesse por quê: a fábrica parecia trabalhar e não produzia nada.
///
/// O 0-E decidiu que o contêiner é o caminho ÚNICO. Nascer desligado contradizia a decisão no
/// único lugar em que ela é aplicada — o padrão.
/// </summary>
public sealed class IsolatedExecutionDefaultsTests
{
    [Fact]
    public void IsolationDefaultsToDockerBecauseTheContainerIsTheOnlyPath()
    {
        Assert.Equal(IsolatedExecutionMode.Docker, new IsolatedExecutionSettings().Mode);
    }

    [Fact]
    public void TurningIsolationOffStaysPossibleButHasToBeDeclared()
    {
        // Desligar continua sendo uma opção de quem instala — o que muda é que passa a ser uma
        // DECLARAÇÃO, e não o estado em que o produto nasce.
        var declarado = new IsolatedExecutionSettings { Mode = IsolatedExecutionMode.Disabled };

        Assert.Equal(IsolatedExecutionMode.Disabled, declarado.Mode);
    }
}
