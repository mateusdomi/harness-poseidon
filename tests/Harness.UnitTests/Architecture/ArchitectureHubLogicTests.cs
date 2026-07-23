using Harness.Modules.Architecture.Application;
using Harness.Modules.Architecture.Contracts;
using static Harness.UnitTests.Architecture.ArchitectureModelFactory;

namespace Harness.UnitTests.Architecture;

/// <summary>
/// Provas PURAS das áreas estendidas do Architecture Hub: agregação de descobertas com confiança +
/// perguntas pendentes (ARC-06), racionalização classificada com impacto — proposta, nunca ação
/// (ARC-07), e a integração Delivery↔Architecture — snapshot, comparação proposta × implementação e
/// consulta de reuso ao portfólio (ARC-10). Tudo determinístico e derivado de fatos gravados.
/// </summary>
public sealed class ArchitectureHubLogicTests
{
    // ARC-06 — descoberta ----------------------------------------------------------------------------

    [Fact]
    public void DiscoverySummaryRollsUpConfidenceConservativelyAndAggregatesPendingQuestions()
    {
        var facts = new[]
        {
            Fact("sys-1", "Billing", "openapi", "high", "open", ["Which team owns it?"]),
            Fact("sys-1", "Billing", "repo", "low", "open", ["Is Postgres still used?", "Which team owns it?"]),
            Fact("sys-1", "Billing", "docs", "high", "confirmed", []),
            Fact("sys-2", "Gateway", "inventory", "medium", "open", []),
        };

        var summary = DiscoveryAggregator.Summarize(facts);

        Assert.Equal(2, summary.Total);
        var billing = Assert.Single(summary.Subjects, s => s.SystemId == "sys-1");
        Assert.Equal(3, billing.DiscoveryCount);
        Assert.Equal(2, billing.OpenCount);
        Assert.Equal(1, billing.ConfirmedCount);
        Assert.Equal("low", billing.OverallConfidence);          // elo mais fraco entre as ABERTAS
        Assert.Equal(2, billing.PendingQuestions.Count);          // deduplicadas
        Assert.Contains("Is Postgres still used?", billing.PendingQuestions);
        Assert.Equal(1, billing.BySource["openapi"]);
        Assert.Equal(1, billing.BySource["repo"]);
    }

    private static DiscoveryAggregator.DiscoveryFact Fact(
        string systemId, string subject, string source, string confidence, string status,
        IReadOnlyList<string> questions) =>
        new(systemId, subject, source, confidence, status, questions);

    // ARC-07 — racionalização ------------------------------------------------------------------------

    [Fact]
    public void RationalizationFlagsFunctionalOverlapAsConsolidate()
    {
        var model = new ArchitectureModelInput(
            [Element("s1", "system", "Billing A"), Element("s2", "system", "Billing B")],
            [],
            [Sys("s1", domain: "payments", capabilities: ["billing"]),
             Sys("s2", domain: "payments", capabilities: ["billing"])]);

        var report = RationalizationAnalyzer.Analyze(model);

        var overlap = Assert.Single(report.Insights, i => i.Category == "functional-overlap");
        Assert.Equal("s1", overlap.SystemId);                 // o de id menor reporta o par
        Assert.Equal("consolidate", overlap.Classification);
        Assert.Contains("s2", overlap.RelatedSystemIds);
    }

    [Fact]
    public void RationalizationClassifiesUnsupportedTechAndCostVsUse()
    {
        var model = new ArchitectureModelInput(
            [Element("s1", "system", "Legacy"), Element("s2", "system", "Idle")],
            [],
            [Sys("s1", criticality: "critical", lifecycleStatus: "eol"),
             Sys("s2", cost: 50_000m)]);   // custo alto e ZERO dependentes

        var report = RationalizationAnalyzer.Analyze(model);

        var tech = Assert.Single(report.Insights, i => i.Category == "unsupported-tech");
        Assert.Equal("replace", tech.Classification);         // eol + critical -> substituir
        var cost = Assert.Single(report.Insights, i => i.Category == "cost-vs-use" && i.SystemId == "s2");
        Assert.Equal("decommission", cost.Classification);
    }

    [Fact]
    public void RationalizationFlagsSpofAndKeepsHealthySystems()
    {
        var model = new ArchitectureModelInput(
            [Element("core", "system", "Core"), Element("a", "system", "A"),
             Element("b", "system", "B"), Element("c", "system", "C")],
            [Relationship("r1", "a", "core", "depends-on"),
             Relationship("r2", "b", "core", "depends-on"),
             Relationship("r3", "c", "core", "calls")],
            [Sys("core", criticality: "critical"), Sys("a"), Sys("b"), Sys("c")]);

        var report = RationalizationAnalyzer.Analyze(model);

        var spof = Assert.Single(report.Insights, i => i.Category == "spof");
        Assert.Equal("core", spof.SystemId);
        Assert.Equal("investigate", spof.Classification);
        Assert.Equal(3, spof.AffectedDependentCount);
        Assert.Contains(report.Insights, i => i.SystemId == "a" && i.Classification == "keep");
    }

    [Fact]
    public void RationalizationFlagsDirectAccessToAnotherSystemsDataStore()
    {
        var model = new ArchitectureModelInput(
            [Element("s1", "system", "Consumer"), Element("s2", "system", "Owner"),
             Element("db", "dataStore", "OwnerDb")],
            [Relationship("r1", "s2", "db", "contains"),   // s2 é dono do banco
             Relationship("r2", "s1", "db", "depends-on")], // s1 acessa o banco alheio
            [Sys("s1"), Sys("s2")]);

        var report = RationalizationAnalyzer.Analyze(model);

        var direct = Assert.Single(report.Insights, i => i.Category == "direct-db-access");
        Assert.Equal("s1", direct.SystemId);
        Assert.Contains("s2", direct.RelatedSystemIds);
    }

    // ARC-10 — baseline / comparação / reuso ---------------------------------------------------------

    [Fact]
    public void SnapshotCapturesOnlyImplementedModel()
    {
        var model = new ArchitectureModelInput(
            [Element("a", "system", "A"),
             Element("p", "system", "Proposed", state: "proposed", proposalId: "prop", changeKind: "add")],
            [Relationship("r1", "a", "a", "uses")],
            []);

        var snapshot = BaselineComparator.Snapshot(model);
        Assert.Single(snapshot.Elements);            // o proposto fica de fora
        Assert.Equal("a", snapshot.Elements[0].Id);
        Assert.Single(snapshot.Edges);
    }

    [Fact]
    public void CompareSeparatesMatchedMissingAndUnplannedWithConformance()
    {
        var baseline = new ArchitectureModelSnapshot(
            [new ArchitectureSnapshotElement("a", "system", "A"),
             new ArchitectureSnapshotElement("b", "system", "B")],
            [new ArchitectureSnapshotEdge("a", "b", "calls")]);
        var asBuilt = new ArchitectureModelSnapshot(
            [new ArchitectureSnapshotElement("a", "system", "A"),
             new ArchitectureSnapshotElement("c", "system", "C")],   // b não construído, c fora do plano
            [new ArchitectureSnapshotEdge("a", "b", "calls")]);

        var comparison = BaselineComparator.Compare("base-1", baseline, asBuilt);

        Assert.Equal(1, comparison.Matched);          // a
        Assert.Equal(1, comparison.Missing);          // b planejado, não construído
        Assert.Equal(1, comparison.Unplanned);        // c construído fora do plano
        Assert.Equal(1, comparison.EdgeMatched);
        Assert.Contains(comparison.Drifts, d => d.ElementId == "b" && d.Drift == "missing");
        Assert.Contains(comparison.Drifts, d => d.ElementId == "c" && d.Drift == "unplanned");
        // Conformidade = (1 elem casado + 1 aresta casada) / (2 elem + 1 aresta planejados) = 66,7%.
        Assert.Equal(66.7, comparison.ConformancePercent);
    }

    [Fact]
    public void FindReuseReturnsSystemsMatchingCapabilityAndDomain()
    {
        var model = new ArchitectureModelInput(
            [Element("s1", "system", "Payments"), Element("s2", "system", "Refunds"),
             Element("s3", "system", "Catalog")],
            [],
            [Sys("s1", domain: "finance", capabilities: ["billing", "refunds"], duplicateOf: "s9"),
             Sys("s2", domain: "finance", capabilities: ["refunds"]),
             Sys("s3", domain: "commerce", capabilities: ["catalog"])]);

        var reuse = BaselineComparator.FindReuse(model, "refunds", "finance");

        Assert.Equal(2, reuse.CandidateCount);
        Assert.Contains(reuse.Candidates, c => c.SystemId == "s1" && c.DuplicateFlag);
        Assert.Contains(reuse.Candidates, c => c.SystemId == "s2");
        Assert.DoesNotContain(reuse.Candidates, c => c.SystemId == "s3");
    }

    [Fact]
    public void FindReuseWithoutFiltersReturnsNothing()
    {
        var model = new ArchitectureModelInput(
            [Element("s1", "system", "A")], [], [Sys("s1", domain: "x", capabilities: ["y"])]);

        var reuse = BaselineComparator.FindReuse(model, null, null);
        Assert.Equal(0, reuse.CandidateCount);
    }
}
