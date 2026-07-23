using Harness.Modules.Architecture.Application;
using static Harness.UnitTests.Architecture.ArchitectureModelFactory;

namespace Harness.UnitTests.Architecture;

/// <summary>
/// Provas PURAS do Architecture Hub: consulta de dependências (ARC-01), views como seleção/reuso
/// (ARC-01), heatmap/mapas (ARC-02), Sistema 360 (ARC-03) e diff/lock/impacto/aplicar (ARC-05).
/// </summary>
public sealed class ArchitectureLogicTests
{
    // ARC-01 — dependências --------------------------------------------------------------------------

    [Fact]
    public void WhoDependsOnReturnsDirectAndTransitiveDependents()
    {
        // web -> api -> db ; worker -> db. Quem depende de db?
        var model = new ArchitectureModelInput(
            [Element("db", "dataStore", "Database"), Element("api", "container", "API"),
             Element("web", "container", "Web"), Element("worker", "container", "Worker")],
            [Relationship("r1", "api", "db", "depends-on"),
             Relationship("r2", "web", "api", "calls"),
             Relationship("r3", "worker", "db", "uses")],
            []);

        var result = DependencyAnalyzer.WhoDependsOn(model, "db");

        // Diretos: api e worker. Transitivo: web (via api).
        Assert.Equal(2, result.DirectCount);
        Assert.Equal(1, result.TransitiveCount);
        Assert.Contains(result.Dependents, d => d.ElementId == "api" && d.Direct);
        Assert.Contains(result.Dependents, d => d.ElementId == "worker" && d.Direct);
        Assert.Contains(result.Dependents, d => d.ElementId == "web" && !d.Direct);
    }

    [Fact]
    public void ContainsEdgeIsNotADependency()
    {
        var model = new ArchitectureModelInput(
            [Element("sys", "system", "System"), Element("comp", "component", "Component")],
            [Relationship("r1", "sys", "comp", "contains")],
            []);

        var result = DependencyAnalyzer.WhoDependsOn(model, "comp");
        Assert.Equal(0, result.DirectCount);
    }

    // ARC-01 — views (seleção + reuso) ---------------------------------------------------------------

    [Fact]
    public void ViewResolvesSubgraphAndReusesElementsAcrossViews()
    {
        var model = new ArchitectureModelInput(
            [Element("a", "system", "A", properties: new Dictionary<string, string> { ["tag"] = "core" }),
             Element("b", "system", "B", properties: new Dictionary<string, string> { ["tag"] = "core" }),
             Element("c", "external", "C")],
            [Relationship("r1", "a", "b", "uses"), Relationship("r2", "b", "c", "calls")],
            []);

        // View 1: só os elementos com tag=core -> a, b e a aresta entre eles.
        var view1 = ArchitectureViewResolver.Resolve(model, new ArchViewSpec(
            "v1", "proj", "Core", "", "c4", [], [], [], ["core"]));
        Assert.Equal(2, view1.Elements.Count);
        Assert.Single(view1.Relationships); // só r1 (ambos extremos na seleção)

        // View 2: seleção explícita b, c -> reusa o MESMO elemento b, sem copiá-lo.
        var view2 = ArchitectureViewResolver.Resolve(model, new ArchViewSpec(
            "v2", "proj", "Edge", "", "archimate", ["b", "c"], [], [], []));
        Assert.Contains(view2.Elements, e => e.Id == "b");
        Assert.Contains(view1.Elements, e => e.Id == "b");
    }

    // ARC-02 — heatmap + mapas -----------------------------------------------------------------------

    [Fact]
    public void HeatmapDerivesSignalsStrictlyFromRecordedMetadata()
    {
        var hot = Sys("s1", criticality: "critical", owner: null, lifecycleStatus: "obsolete",
            cost: 50_000m, incidents: 3, busFactor: 1, risk: "high");
        var signals = HeatmapCalculator.Signals(hot).Select(s => s.Code).ToArray();

        Assert.Contains("critical", signals);
        Assert.Contains("no-owner", signals);
        Assert.Contains("tech-obsolete", signals);
        Assert.Contains("cost", signals);
        Assert.Contains("incidents", signals);
        Assert.Contains("bus-factor", signals);
        Assert.Contains("risk", signals);
        Assert.Contains("no-doc", signals);

        var healthy = Sys("s2", owner: "team",
            documents: [new ArchDocumentLink("d1", "ADR-1", "adr", "current")]);
        Assert.Empty(HeatmapCalculator.Signals(healthy));
    }

    [Fact]
    public void IntegrationGraphOnlyLinksSystems()
    {
        var model = new ArchitectureModelInput(
            [Element("s1", "system", "S1"), Element("s2", "system", "S2"),
             Element("c1", "component", "C1")],
            [Relationship("r1", "s1", "s2", "flows-to"), Relationship("r2", "s1", "c1", "contains")],
            [Sys("s1"), Sys("s2")]);

        var graph = SystemsMapProjector.IntegrationGraph(model);
        Assert.Equal(2, graph.SystemCount);
        Assert.Equal(1, graph.EdgeCount); // só a aresta sistema->sistema
    }

    [Fact]
    public void DomainAndCapabilityMapsGroupSystems()
    {
        var model = new ArchitectureModelInput(
            [Element("s1", "system", "S1"), Element("s2", "system", "S2")],
            [],
            [Sys("s1", domain: "payments", capabilities: ["billing"]),
             Sys("s2", domain: "payments", capabilities: ["billing", "refunds"])]);

        var domains = SystemsMapProjector.DomainMap(model);
        Assert.Single(domains.Domains);
        Assert.Equal(2, domains.Domains[0].SystemCount);

        var capabilities = SystemsMapProjector.CapabilityMap(model);
        Assert.Contains(capabilities.Capabilities, c => c.Capability == "billing" && c.SystemCount == 2);
        Assert.Contains(capabilities.Capabilities, c => c.Capability == "refunds" && c.SystemCount == 1);
    }

    // ARC-03 — Sistema 360 ---------------------------------------------------------------------------

    [Fact]
    public void System360AssemblesTypedSections()
    {
        var model = new ArchitectureModelInput(
            [Element("s1", "system", "Billing"), Element("c1", "component", "Ledger"),
             Element("s2", "system", "Gateway")],
            [Relationship("r1", "s1", "c1", "contains"), Relationship("r2", "s1", "s2", "calls")],
            [Sys("s1", criticality: "high", domain: "payments", capabilities: ["billing"],
                documents: [new ArchDocumentLink("d1", "ADR-1", "adr", "current")])]);

        var overview = System360Projector.Project(model, "s1");
        Assert.NotNull(overview);
        Assert.Equal("payments", overview!.Business.Domain);
        Assert.Equal("high", overview.Business.Criticality);
        Assert.Single(overview.Technology.Containers); // Ledger via 'contains'
        Assert.Single(overview.Integrations.Outgoing);  // calls Gateway
        Assert.Single(overview.Governance.Documents);
        Assert.Equal("99.9%", overview.Operation.Sla);
        Assert.Null(System360Projector.Project(model, "c1")); // não é sistema
    }

    // ARC-05 — diff / lock / impacto / aplicar -------------------------------------------------------

    [Fact]
    public void DiffSeparatesProposedFromImplementedAndCountsChanges()
    {
        var model = new ArchitectureModelInput(
            [Element("a", "system", "A"),
             Element("p1", "system", "A renamed", state: "proposed", proposalId: "prop",
                counterpartId: "a", changeKind: "modify"),
             Element("p2", "component", "New", state: "proposed", proposalId: "prop", changeKind: "add")],
            [], [Sys("a")]);

        var diff = ArchitectureProposalPlanner.Diff(model, "prop");
        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Modified);
        Assert.Equal(0, diff.Removed);
        Assert.Contains(diff.Changes, c => c.CounterpartId == "a" && c.FieldChanges.Contains("name"));
    }

    [Fact]
    public void LockedElementIsNeverOverwrittenByProposedChange()
    {
        var model = new ArchitectureModelInput(
            [Element("a", "system", "A", locked: true),
             Element("p1", "system", "A hacked", state: "proposed", proposalId: "prop",
                counterpartId: "a", changeKind: "modify")],
            [], [Sys("a")]);

        var diff = ArchitectureProposalPlanner.Diff(model, "prop");
        Assert.Equal(1, diff.BlockedByLock);

        var plan = ArchitectureProposalPlanner.PlanApply(model, "prop", "ok", DateTimeOffset.UtcNow);
        Assert.True(plan.ShouldApply);
        Assert.Empty(plan.ElementUpserts);           // o travado não é sobrescrito
        Assert.Equal(1, plan.Result.SkippedLocked);
        Assert.Empty(plan.Result.AppliedChanges);
    }

    [Fact]
    public void CriticalChangeRequiresJustificationBeforeApplying()
    {
        var model = new ArchitectureModelInput(
            [Element("a", "system", "A"),
             Element("p1", "system", "A", state: "proposed", proposalId: "prop",
                counterpartId: "a", changeKind: "remove")],
            [], [Sys("a", criticality: "critical")]);

        var noJustification = ArchitectureProposalPlanner.PlanApply(model, "prop", null, DateTimeOffset.UtcNow);
        Assert.False(noJustification.ShouldApply);
        Assert.Equal("justification_required", noJustification.Result.Status);

        var withJustification = ArchitectureProposalPlanner.PlanApply(
            model, "prop", "decommissioning legacy", DateTimeOffset.UtcNow);
        Assert.True(withJustification.ShouldApply);
        Assert.Contains("a", withJustification.ElementRemovals);
    }

    [Fact]
    public void ImpactListsAffectedDependentsAndDocumentsBeforeApplying()
    {
        var model = new ArchitectureModelInput(
            [Element("a", "system", "A"), Element("b", "system", "B"),
             Element("p1", "system", "A v2", state: "proposed", proposalId: "prop",
                counterpartId: "a", changeKind: "modify")],
            [Relationship("r1", "b", "a", "depends-on")],
            [Sys("a", documents: [new ArchDocumentLink("d1", "A design", "design", "current")])]);

        var impact = ArchitectureProposalPlanner.Impact(model, "prop");
        Assert.Contains(impact.AffectedDependents, d => d.ElementId == "b"); // B depende de A
        Assert.Contains(impact.AffectedDocuments, d => d.Id == "d1");        // doc de A é afetado
    }
}
