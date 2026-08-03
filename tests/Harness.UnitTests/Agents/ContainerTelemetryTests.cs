using Harness.Host.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// Telemetria não é trabalho, e dentro da sandbox ela custa muito mais que fora.
///
/// Observado ao vivo em 2026-08-03: uma tentativa ficou dezesseis minutos sem produzir um
/// token. O log do proxy explicava — centenas de `proxy-deny statsig.anthropic.com:443` e
/// NENHUMA conexão ao endpoint do modelo. A CLI tentava telemetria antes do trabalho, o
/// egresso restrito negava corretamente, e ela reentrava no retry. O agente não estava
/// pensando: estava tentando avisar que tinha começado.
///
/// A garantia fica aqui porque a alternativa — abrir o egresso para o domínio de telemetria —
/// é a correção do sintoma, e porque perder uma dessas variáveis devolve o laço em silêncio.
/// </summary>
public sealed class ContainerTelemetryTests
{
    [Theory]
    [InlineData("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC")]
    [InlineData("DISABLE_TELEMETRY")]
    [InlineData("DISABLE_ERROR_REPORTING")]
    [InlineData("DISABLE_AUTOUPDATER")]
    public void TheSandboxTurnsOffTrafficThatIsNotWork(string variable)
    {
        Assert.True(
            AgentRunOrchestrator.NonEssentialTrafficOff.TryGetValue(variable, out var value),
            $"{variable} precisa estar desligada dentro do contêiner.");
        Assert.Equal("1", value);
    }
}
