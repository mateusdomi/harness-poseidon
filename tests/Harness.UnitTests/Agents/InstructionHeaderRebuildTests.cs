using Harness.Host.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// OPS-069 — o cabeçalho de roteamento é fato derivável, não história a preservar.
///
/// A instrução corretiva era montada a partir do corpo anterior inteiro, e carregava adiante as
/// três linhas de topo — papel, persona e tipo de card. Um erro de roteamento na versão 1 virava
/// permanente: a correção o recopiava a cada reprovação, e nem o replanejamento o alcançava.
///
/// Medido em 04/08/2026: os cinco cards de implementação da fase 5 nasceram seis minutos antes do
/// conserto que tirava a persona de descoberta da fatia de código. Chegaram à décima versão de
/// instrução ainda mandando um Product Owner — cujo escopo declarado NEGA `src/**` — implementar
/// backend. O ator obedeceu a persona e entregou diff vazio; três cards pararam bloqueados e a
/// fase 5 ficou duas horas e meia sem andar.
/// </summary>
public sealed class InstructionHeaderRebuildTests
{
    private const string Header =
        "Capacidade de execução autorizada: backend-specialist\n" +
        "Especialidade exigida: playbook-dev-executor\n" +
        "Tipo de card: agent_task";

    [Fact]
    public void OCabecalhoErradoEhSubstituidoPeloAtual()
    {
        var previous =
            "Capacidade de execução autorizada: backend-specialist\n" +
            "Especialidade exigida: playbook-product-owner\n" +
            "Tipo de card: agent_task\n\n" +
            "Implementar a fatia de servidor.";

        var rebuilt = ChiefBacklogLoopService.RebuildInstructionHeader(
            previous, "backend-specialist", "playbook-dev-executor", "agent_task");

        Assert.StartsWith(Header, rebuilt, StringComparison.Ordinal);
        Assert.DoesNotContain("playbook-product-owner", rebuilt, StringComparison.Ordinal);
        Assert.Contains("Implementar a fatia de servidor.", rebuilt, StringComparison.Ordinal);
    }

    /// <summary>
    /// O corpo é onde mora o trabalho — critérios de aceite, fronteiras, achados de rodadas
    /// anteriores. Perder uma linha dele para trocar um cabeçalho seria um remédio pior que a doença.
    /// </summary>
    [Fact]
    public void OCorpoSobreviveIntacto()
    {
        var body =
            "Implementar a fatia de servidor.\n\n" +
            "Em escopo: domínio, aplicação, persistência.\n" +
            "Fora de escopo: qualquer UI.\n\n" +
            "Critérios de aceite:\n" +
            "- É possível registrar um empréstimo\n" +
            "- É possível registrar a devolução\n\n" +
            "## Correções exigidas pelo review independente (tentativa 01ABC)\n" +
            "O diff apresentado é totalmente vazio.";

        var rebuilt = ChiefBacklogLoopService.RebuildInstructionHeader(
            $"Capacidade de execução autorizada: x\nEspecialidade exigida: y\nTipo de card: z\n\n{body}",
            "backend-specialist", "playbook-dev-executor", "agent_task");

        Assert.EndsWith(body, rebuilt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Texto sem cabeçalho reconhecível GANHA um. Remover linhas por palpite seria apagar trabalho;
    /// a assimetria é deliberada — acrescentar é reversível, apagar não é.
    /// </summary>
    [Fact]
    public void TextoSemCabecalhoGanhaUmSemPerderNada()
    {
        const string previous = "Implementar a fatia de servidor.\nSem cabeçalho nenhum.";

        var rebuilt = ChiefBacklogLoopService.RebuildInstructionHeader(
            previous, "backend-specialist", "playbook-dev-executor", "agent_task");

        Assert.StartsWith(Header, rebuilt, StringComparison.Ordinal);
        Assert.EndsWith(previous, rebuilt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A remoção acontece só no TOPO. Um achado do crítico que cite "Tipo de card:" no meio do
    /// texto não pode ser engolido — seria o gate destruindo a evidência que ele mesmo produziu.
    /// </summary>
    [Fact]
    public void MencaoAoCabecalhoNoMEIODoTextoNaoEhRemovida()
    {
        var previous =
            "Capacidade de execução autorizada: x\nEspecialidade exigida: y\nTipo de card: z\n\n" +
            "Implementar.\n\n" +
            "## Correções exigidas\n" +
            "Tipo de card: o parecer citou o cabeçalho e isto precisa sobreviver.";

        var rebuilt = ChiefBacklogLoopService.RebuildInstructionHeader(
            previous, "backend-specialist", "playbook-dev-executor", "agent_task");

        Assert.Contains(
            "Tipo de card: o parecer citou o cabeçalho e isto precisa sobreviver.",
            rebuilt,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Reaplicar a mesma reconstrução não empilha cabeçalhos. A correção roda a cada reprovação;
    /// se cada volta somasse três linhas, a instrução cresceria sozinha até virar ruído.
    /// </summary>
    [Fact]
    public void ReconstruirDuasVezesNaoEmpilhaCabecalho()
    {
        const string previous =
            "Capacidade de execução autorizada: a\nEspecialidade exigida: b\nTipo de card: c\n\nCorpo.";

        var once = ChiefBacklogLoopService.RebuildInstructionHeader(
            previous, "backend-specialist", "playbook-dev-executor", "agent_task");
        var twice = ChiefBacklogLoopService.RebuildInstructionHeader(
            once, "backend-specialist", "playbook-dev-executor", "agent_task");

        Assert.Equal(once, twice);
        Assert.Equal(1, once.Split("Especialidade exigida:").Length - 1);
    }

    [Fact]
    public void CabecalhoSemCorpoNaoDeixaLinhaSolta()
    {
        var rebuilt = ChiefBacklogLoopService.RebuildInstructionHeader(
            "Capacidade de execução autorizada: a\nEspecialidade exigida: b\nTipo de card: c\n\n",
            "backend-specialist", "playbook-dev-executor", "agent_task");

        Assert.Equal(Header, rebuilt);
    }

    /// <summary>Fim de linha do Windows não pode virar cabeçalho irreconhecível.</summary>
    [Fact]
    public void FimDeLinhaWindowsEhTratado()
    {
        var rebuilt = ChiefBacklogLoopService.RebuildInstructionHeader(
            "Capacidade de execução autorizada: a\r\nEspecialidade exigida: b\r\nTipo de card: c\r\n\r\nCorpo.",
            "backend-specialist", "playbook-dev-executor", "agent_task");

        Assert.Equal($"{Header}\n\nCorpo.", rebuilt);
    }
}
