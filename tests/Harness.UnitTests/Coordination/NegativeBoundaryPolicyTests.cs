using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class NegativeBoundaryPolicyTests
{
    private static readonly CardScope[] Plan =
    [
        new("card-back", "Motor de cobrança", ["src/Modules/Billing"]),
        new("card-front", "Tela de cobrança", ["frontend/src/features/billing"]),
        new("card-shared", "Contratos compartilhados", ["src/Harness.SharedKernel", "docs/contracts"])
    ];

    /// <summary>Gate da fase: a fronteira nomeia o card irmão dono do escopo.</summary>
    [Fact]
    public void BoundariesNameTheSiblingThatOwnsTheScope()
    {
        var boundaries = NegativeBoundaryPolicy.Derive("card-back", Plan);

        Assert.Equal(
            ["docs/contracts", "frontend/src/features/billing", "src/Harness.SharedKernel"],
            boundaries.Select(boundary => boundary.Scope));
        var front = boundaries.Single(b => b.Scope == "frontend/src/features/billing");
        Assert.Equal("card-front", front.OwnerCardId);
        Assert.Contains("Tela de cobrança", front.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ACardIsNeverFencedOutOfItsOwnScope()
    {
        var boundaries = NegativeBoundaryPolicy.Derive("card-back", Plan);

        // Fronteira contra si mesmo travaria o trabalho e viraria escalação inútil.
        Assert.DoesNotContain("src/Modules/Billing", boundaries.Select(b => b.Scope));
    }

    [Fact]
    public void ScopeSharedByTwoCardsIsNotAFenceForEither()
    {
        CardScope[] plan =
        [
            new("card-a", "A", ["src/Comum", "src/A"]),
            new("card-b", "B", ["src/Comum", "src/B"])
        ];

        Assert.DoesNotContain("src/Comum", NegativeBoundaryPolicy.Derive("card-a", plan).Select(b => b.Scope));
        Assert.DoesNotContain("src/Comum", NegativeBoundaryPolicy.Derive("card-b", plan).Select(b => b.Scope));
    }

    [Fact]
    public void ASoloCardHasNoBoundariesAndNoInventedWarning()
    {
        var boundaries = NegativeBoundaryPolicy.Derive(
            "card-only", [new CardScope("card-only", "Sozinho", ["src/X"])]);

        Assert.Empty(boundaries);
        // Instrução sem função ensina o agente a ignorar instruções.
        Assert.Equal(string.Empty, NegativeBoundaryPolicy.ComposeBundleSection(boundaries));
    }

    /// <summary>Gate da fase: a fronteira chega ao bundle do agente.</summary>
    [Fact]
    public void TheBundleSectionExplainsWhyAndNotOnlyWhat()
    {
        var section = NegativeBoundaryPolicy.ComposeBundleSection(
            NegativeBoundaryPolicy.Derive("card-back", Plan));

        Assert.Contains("Fronteiras desta tarefa", section, StringComparison.Ordinal);
        Assert.Contains("em paralelo", section, StringComparison.Ordinal);
        Assert.Contains("relate em vez de corrigir", section, StringComparison.Ordinal);
        Assert.Contains("card-front", section, StringComparison.Ordinal);
    }

    [Fact]
    public void TouchingASiblingScopeIsDetectedAsAViolation()
    {
        var boundaries = NegativeBoundaryPolicy.Derive("card-back", Plan);

        var violation = NegativeBoundaryPolicy.FindViolation(
            "frontend/src/features/billing/components/invoice.tsx", boundaries);

        Assert.NotNull(violation);
        Assert.Equal("card-front", violation!.OwnerCardId);
    }

    [Fact]
    public void TouchingOwnScopeIsNotAViolation()
    {
        var boundaries = NegativeBoundaryPolicy.Derive("card-back", Plan);

        Assert.Null(NegativeBoundaryPolicy.FindViolation(
            "src/Modules/Billing/Application/Charge.cs", boundaries));
    }

    [Fact]
    public void ScopePrefixDoesNotMatchAnUnrelatedSiblingDirectory()
    {
        var boundaries = NegativeBoundaryPolicy.Derive("card-back", Plan);

        // 'docs/contracts' é fronteira; 'docs/contractsX' é outro diretório e não pode casar.
        Assert.Null(NegativeBoundaryPolicy.FindViolation("docs/contractsX/leia.md", boundaries));
        Assert.NotNull(NegativeBoundaryPolicy.FindViolation("docs/contracts/openapi.json", boundaries));
    }

    [Fact]
    public void TheScopeItselfCountsAsTouched()
    {
        var boundaries = NegativeBoundaryPolicy.Derive("card-back", Plan);
        Assert.NotNull(NegativeBoundaryPolicy.FindViolation("docs/contracts", boundaries));
    }

    [Fact]
    public void ACardOutsideThePlanIsRejected()
    {
        Assert.Throws<ArgumentException>(() => NegativeBoundaryPolicy.Derive("card-fantasma", Plan));
    }

    /// <summary>Gate da fase: o planner preenche o OutOfScope de cada card com as dos irmãos.</summary>
    [Fact]
    public void ThePlannerFillsOutOfScopeWithTheSiblingBoundaries()
    {
        var proposal = new DemandPlanProposal("feature-1",
        [
            Card("Motor de cobrança", "src/Modules/Billing", "Não mexa em migrations."),
            Card("Tela de cobrança", "frontend/src/features/billing", "")
        ]);

        var enriched = NegativeBoundaryPolicy.EnrichWithSiblingBoundaries(proposal);

        var back = enriched.Cards[0];
        // O texto do planner é preservado: apagá-lo perderia informação que só ele tinha.
        Assert.StartsWith("Não mexa em migrations.", back.OutOfScope, StringComparison.Ordinal);
        Assert.Contains("frontend/src/features/billing", back.OutOfScope, StringComparison.Ordinal);
        Assert.Contains("Tela de cobrança", back.OutOfScope, StringComparison.Ordinal);

        var front = enriched.Cards[1];
        Assert.Contains("src/Modules/Billing", front.OutOfScope, StringComparison.Ordinal);
        Assert.DoesNotContain("frontend/src/features/billing", front.OutOfScope, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleCardPlanIsReturnedUntouched()
    {
        var proposal = new DemandPlanProposal("feature-1", [Card("Só ela", "src/X", "nada")]);

        Assert.Same(proposal, NegativeBoundaryPolicy.EnrichWithSiblingBoundaries(proposal));
    }

    [Fact]
    public void CardsSharingTheSameScopeGetNoBoundaryAgainstEachOther()
    {
        var proposal = new DemandPlanProposal("feature-1",
        [
            Card("A", "src/Comum", ""),
            Card("B", "src/Comum", "")
        ]);

        var enriched = NegativeBoundaryPolicy.EnrichWithSiblingBoundaries(proposal);

        Assert.All(enriched.Cards, card => Assert.Equal(string.Empty, card.OutOfScope));
    }

    private static ProposedCard Card(string title, string inScope, string outOfScope) => new(
        title, "tarefa", "backend", "Instrução.", inScope, outOfScope, [], [], []);
}
