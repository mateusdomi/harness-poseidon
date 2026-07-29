using Harness.Modules.Coordination.Application;
using Harness.SharedKernel.CodeGraph;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// Grafo derivado + raio de impacto (B6/F15). O que estes testes protegem, acima de tudo, é a
/// distinção entre "medi e o impacto é pequeno" e "não medi" — as duas respostas cabem no mesmo
/// número zero, e é aí que um gate honesto vira um gate decorativo.
/// </summary>
public sealed class CodeGraphBlastRadiusTests
{
    private const string Nucleo = "T:App.Nucleo";
    private const string Meio = "T:App.Meio";
    private const string Borda = "T:App.Borda";
    private const string Solto = "T:App.Solto";

    /// <summary>
    /// Nucleo &lt;- Meio &lt;- Borda (cada um depende do anterior) e Solto, sem ninguém.
    /// </summary>
    private static CodeGraph Cadeia() => CodeGraph.Create(
        [
            new CodeGraphNode(Nucleo, "App.Nucleo", CodeGraphNodeKind.Type, "src/nucleo.cs", "App"),
            new CodeGraphNode(Meio, "App.Meio", CodeGraphNodeKind.Type, "src/meio.cs", "App"),
            new CodeGraphNode(Borda, "App.Borda", CodeGraphNodeKind.Type, "src/borda.cs", "App"),
            new CodeGraphNode(Solto, "App.Solto", CodeGraphNodeKind.Type, "src/solto.cs", "App")
        ],
        [
            new CodeGraphEdge(Meio, Nucleo, CodeGraphEdgeKind.References),
            new CodeGraphEdge(Borda, Meio, CodeGraphEdgeKind.References)
        ]);

    [Fact]
    public void DependentsAreTransitiveAndExcludeTheNodeItself()
    {
        var graph = Cadeia();

        // Mudar o núcleo quebra o meio e, por consequência, a borda.
        Assert.Equal([Borda, Meio], graph.DependentsOf(Nucleo));
        Assert.Equal([Borda], graph.DependentsOf(Meio));
        Assert.Empty(graph.DependentsOf(Borda));
    }

    [Fact]
    public void ACycleDoesNotHangTheTraversal()
    {
        var graph = CodeGraph.Create(
            [
                new CodeGraphNode("T:A", "A", CodeGraphNodeKind.Type, "a.cs", "M"),
                new CodeGraphNode("T:B", "B", CodeGraphNodeKind.Type, "b.cs", "M")
            ],
            [
                new CodeGraphEdge("T:A", "T:B", CodeGraphEdgeKind.References),
                new CodeGraphEdge("T:B", "T:A", CodeGraphEdgeKind.References)
            ]);

        // Código real tem dependência mútua; um gate não pode entrar em laço por causa disso.
        Assert.Equal(["T:B"], graph.DependentsOf("T:A"));
        Assert.Equal(["T:A"], graph.DependentsOf("T:B"));
    }

    [Fact]
    public void ContainmentIsNotDependency()
    {
        var graph = CodeGraph.Create(
            [
                new CodeGraphNode("M:App", "App", CodeGraphNodeKind.Module, string.Empty, "App"),
                new CodeGraphNode(Nucleo, "App.Nucleo", CodeGraphNodeKind.Type, "src/nucleo.cs", "App")
            ],
            [new CodeGraphEdge("M:App", Nucleo, CodeGraphEdgeKind.Contains)]);

        // O módulo agrupa o tipo; ele não quebra quando o tipo muda. Contar 'contains' como
        // dependência inflaria o raio de impacto de TODO card com o módulo inteiro.
        Assert.Empty(graph.DependentsOf(Nucleo));
    }

    [Fact]
    public void EdgesLeavingTheIndexedSetAreDropped()
    {
        var graph = CodeGraph.Create(
            [new CodeGraphNode(Nucleo, "App.Nucleo", CodeGraphNodeKind.Type, "src/nucleo.cs", "App")],
            [new CodeGraphEdge(Nucleo, "T:System.String", CodeGraphEdgeKind.References)]);

        Assert.Empty(graph.Edges);
        Assert.Single(graph.Nodes);
    }

    [Fact]
    public void TheDigestDependsOnContentAndNotOnTheOrderTheNodesArrived()
    {
        var nodes = Cadeia().Nodes.ToArray();
        var edges = Cadeia().Edges.ToArray();

        var direct = CodeGraph.Create(nodes, edges);
        var shuffled = CodeGraph.Create(nodes.Reverse(), edges.Reverse());

        // É esta propriedade que permite perguntar "o grafo mudou?": se a ordem de leitura dos
        // arquivos alterasse o digest, toda reconstrução pareceria uma mudança de código.
        Assert.Equal(direct.Digest, shuffled.Digest);
    }

    [Fact]
    public void TheDigestChangesWhenAnEdgeAppears()
    {
        var withoutEdge = CodeGraph.Create(Cadeia().Nodes, []);

        Assert.NotEqual(Cadeia().Digest, withoutEdge.Digest);
    }

    [Fact]
    public void APartialTypeIsOneNodeReachableByEveryFileThatDeclaresIt()
    {
        var graph = CodeGraph.Create(
            [
                new CodeGraphNode(Nucleo, "App.Nucleo", CodeGraphNodeKind.Type, "src/nucleo.cs", "App"),
                new CodeGraphNode(Nucleo, "App.Nucleo", CodeGraphNodeKind.Type, "src/nucleo.parte2.cs", "App")
            ],
            []);

        Assert.Single(graph.Nodes);
        Assert.Equal([Nucleo], graph.NodesInFile("src/nucleo.cs"));
        // Sem isto, tocar o segundo arquivo de um tipo parcial seria lido como "fora do índice".
        Assert.Equal([Nucleo], graph.NodesInFile("src/nucleo.parte2.cs"));
    }

    [Fact]
    public void PathsAreComparedNormalizedSoASeparatorDoesNotHideImpact()
    {
        var radius = Cadeia().MeasureBlastRadius([@".\src\nucleo.cs"]);

        Assert.Equal([Nucleo], radius.TouchedNodeIds);
        Assert.Equal(2, radius.DependentCount);
        Assert.True(radius.IsFullyMeasured);
    }

    [Fact]
    public void APathOutsideTheIndexIsUnknownImpactAndNeverZero()
    {
        var radius = Cadeia().MeasureBlastRadius(["frontend/src/pages/tela.tsx"]);

        Assert.Empty(radius.TouchedNodeIds);
        Assert.Equal(0, radius.DependentCount);
        // O número é zero, mas a resposta NÃO é "não impacta" — e a diferença está registrada.
        Assert.False(radius.IsFullyMeasured);
        Assert.Equal(["frontend/src/pages/tela.tsx"], radius.UnindexedPaths);
    }

    [Fact]
    public void TouchedNodesAreNotCountedAsTheirOwnDependents()
    {
        var radius = Cadeia().MeasureBlastRadius(["src/nucleo.cs", "src/meio.cs"]);

        Assert.Equal([Meio, Nucleo], radius.TouchedNodeIds);
        Assert.Equal([Borda], radius.DependentNodeIds);
    }

    // --- Raio de impacto → risco -------------------------------------------------------------

    [Fact]
    public void CouplingRaisesTheRiskAndTheJustificationCarriesTheNumbers()
    {
        var radius = new CodeBlastRadius(
            ["T:App.Nucleo"],
            [.. Enumerable.Range(0, BlastRadiusPolicy.HighThreshold).Select(i => $"T:App.Dep{i}")],
            []);

        var assessment = BlastRadiusPolicy.Assess(RiskTier.Low, radius);

        Assert.Equal(RiskTier.High, assessment.EffectiveRisk);
        Assert.True(assessment.Escalated);
        Assert.Equal(BlastRadiusPolicy.ReasonEscalated, assessment.ReasonCode);
        // Auditável = a justificativa mostra a conta, não só o veredito.
        Assert.Contains("20 dependente(s)", assessment.Justification, StringComparison.Ordinal);
        Assert.Contains("risco sobe de Low para High", assessment.Justification, StringComparison.Ordinal);
    }

    [Fact]
    public void RiskIsNeverLoweredByASmallBlastRadius()
    {
        var assessment = BlastRadiusPolicy.Assess(
            RiskTier.Critical,
            new CodeBlastRadius(["T:App.Solto"], [], []));

        // O grafo só sabe de acoplamento. Contrato, dado sensível e obrigação externa continuam
        // valendo, e nenhum deles aparece numa contagem de dependentes.
        Assert.Equal(RiskTier.Critical, assessment.EffectiveRisk);
        Assert.False(assessment.Escalated);
        Assert.Equal(BlastRadiusPolicy.ReasonConfirmed, assessment.ReasonCode);
        Assert.Contains("nunca rebaixa", assessment.Justification, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatWasNotMeasuredIsDeclaredAsNotMeasuredWithTheOffendingPaths()
    {
        var assessment = BlastRadiusPolicy.Assess(
            RiskTier.Medium,
            new CodeBlastRadius([], [], ["frontend/src/app.tsx"]));

        Assert.False(assessment.Measured);
        Assert.Equal(BlastRadiusPolicy.ReasonUnmeasured, assessment.ReasonCode);
        Assert.Contains("IMPACTO NÃO MEDIDO", assessment.Justification, StringComparison.Ordinal);
        Assert.Contains("frontend/src/app.tsx", assessment.Justification, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewDepthComesFromTheEffortPolicyAppliedToTheEffectiveRisk()
    {
        var radius = new CodeBlastRadius(
            ["T:App.Nucleo"],
            [.. Enumerable.Range(0, BlastRadiusPolicy.CriticalThreshold).Select(i => $"T:App.Dep{i}")],
            []);

        var assessment = BlastRadiusPolicy.Assess(RiskTier.Low, radius);

        // Uma segunda tabela de esforço divergiria da primeira no primeiro ajuste.
        Assert.Equal(RiskTier.Critical, assessment.EffectiveRisk);
        Assert.Equal(
            EffortPolicy.Decide(WorkNature.Implementation, RiskTier.Critical).ReviewDepth,
            assessment.ReviewDepth);
    }

    [Theory]
    [InlineData(0, RiskTier.Low)]
    [InlineData(BlastRadiusPolicy.MediumThreshold, RiskTier.Medium)]
    [InlineData(BlastRadiusPolicy.HighThreshold, RiskTier.High)]
    [InlineData(BlastRadiusPolicy.CriticalThreshold, RiskTier.Critical)]
    public void TheThresholdsAreDeclaredStepsAndTheSameForEveryCard(int dependents, RiskTier expected) =>
        Assert.Equal(expected, BlastRadiusPolicy.RiskFromDependents(dependents));
}
