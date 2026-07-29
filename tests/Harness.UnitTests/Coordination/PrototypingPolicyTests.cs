using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class PrototypingIntakePolicyTests
{
    private static readonly OrganizationDesignAssets Nothing = new(false, false, false);
    private static readonly OrganizationDesignAssets Everything = new(true, true, true);

    /// <summary>Gate da fase, caminho 1: prosa sem nada cadastrado — a Bruna pergunta o que falta.</summary>
    [Fact]
    public void ProseWithoutAssetsAsksForIdentityAndScreens()
    {
        var reading = PrototypingIntakePolicy.Read([], Nothing);

        Assert.Equal(PrototypingEntryPath.Prose, reading.Path);
        Assert.Equal(
            [PrototypingIntakePolicy.QuestionBrand, PrototypingIntakePolicy.QuestionScreens],
            reading.Questions);
        Assert.Empty(reading.Inherited);
    }

    [Fact]
    public void ProseInAnOrganizationThatAlreadyHasIdentityAsksNothing()
    {
        var reading = PrototypingIntakePolicy.Read([], Everything);

        // Interrogar quem já respondeu é o que separa um formulário de uma conversa.
        Assert.Empty(reading.Questions);
        Assert.Equal(
            PrototypingIntakePolicy.ReasonInheritedFromOrganization, reading.ReasonCode);
        Assert.Contains(PrototypingIntakePolicy.InheritedBrand, reading.Inherited);
    }

    [Fact]
    public void ProseThatAlreadyDescribesScreensIsNotAskedAboutThem()
    {
        var reading = PrototypingIntakePolicy.Read(
            [], new OrganizationDesignAssets(true, false, false), mentionsScreens: true);

        Assert.Empty(reading.Questions);
    }

    /// <summary>Gate da fase, caminho 2: documento de requisitos com especificação de telas.</summary>
    [Fact]
    public void ARequirementsDocumentIsDetectedAndOnlyTheGapIsAsked()
    {
        var reading = PrototypingIntakePolicy.Read(
            [new IntakeAttachment("requisitos.md", "text/markdown", 4_000)], Nothing);

        Assert.Equal(PrototypingEntryPath.RequirementsDocument, reading.Path);
        Assert.Equal([PrototypingIntakePolicy.QuestionBrand], reading.Questions);
    }

    [Fact]
    public void ARequirementsDocumentWithBrandAlreadyCadastradaAsksNothing()
    {
        var reading = PrototypingIntakePolicy.Read(
            [new IntakeAttachment("requisitos.pdf", "application/pdf", 9_000)],
            new OrganizationDesignAssets(true, false, false));

        Assert.Empty(reading.Questions);
        Assert.Equal(PrototypingIntakePolicy.ReasonSpecificationProvided, reading.ReasonCode);
    }

    /// <summary>Gate da fase, caminho 3: ZIP React anexado responde a pergunta visual inteira.</summary>
    [Fact]
    public void AReactBundleAnswersEverythingVisualAndNothingIsAsked()
    {
        var reading = PrototypingIntakePolicy.Read(
            [new IntakeAttachment("telas.zip", "application/zip", 2_000_000)], Nothing);

        Assert.Equal(PrototypingEntryPath.ReactBundle, reading.Path);
        Assert.Empty(reading.Questions);
        Assert.Contains(PrototypingIntakePolicy.InheritedFromBundle, reading.Inherited);
        Assert.Equal(PrototypingIntakePolicy.ReasonBundleProvided, reading.ReasonCode);
    }

    [Fact]
    public void TheBundleWinsOverADocumentWhenBothArrive()
    {
        var reading = PrototypingIntakePolicy.Read(
        [
            new IntakeAttachment("requisitos.md", "text/markdown", 1_000),
            new IntakeAttachment("telas.zip", "application/zip", 1_000_000)
        ], Nothing);

        Assert.Equal(PrototypingEntryPath.ReactBundle, reading.Path);
    }

    [Fact]
    public void WhatWasInheritedIsAlwaysDeclaredNeverSilent()
    {
        var reading = PrototypingIntakePolicy.Read([], Everything);

        // O dono não pode descobrir depois que a marca do projeto veio de outro lugar.
        Assert.Equal(3, reading.Inherited.Count);
        Assert.All(reading.Inherited, item => Assert.False(string.IsNullOrWhiteSpace(item)));
    }
}

public sealed class ReactBundleValidatorTests
{
    private static readonly IntakeAttachment Zip = new("telas.zip", "application/zip", 1_000_000);

    [Fact]
    public void AValidBundleIsAccepted()
    {
        var result = ReactBundleValidator.Validate(Zip, ["src/App.tsx", "package.json"]);
        Assert.True(result.IsValid);
    }

    /// <summary>Gate da fase: ZIP inválido é recusado com mensagem de NEGÓCIO.</summary>
    [Fact]
    public void AnArchiveWithoutScreensIsRefusedInPlainLanguage()
    {
        var result = ReactBundleValidator.Validate(Zip, ["leia-me.txt", "notas/rascunho.docx"]);

        Assert.False(result.IsValid);
        Assert.Equal(BundleRejection.MissingFrontendEntry, result.Rejection);
        // Quem anexa o ZIP é o dono, não um desenvolvedor: o motivo tem de dizer o que fazer.
        Assert.Contains("telas", result.BusinessMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entry", result.BusinessMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".tsx", result.BusinessMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingThatIsNotAnArchiveIsRefusedWithGuidance()
    {
        var result = ReactBundleValidator.Validate(
            new IntakeAttachment("telas.rar", "application/x-rar", 1_000), ["a.tsx"]);

        Assert.Equal(BundleRejection.NotAnArchive, result.Rejection);
        Assert.Contains(".zip", result.BusinessMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyBundleIsRefused()
    {
        Assert.Equal(BundleRejection.Empty, ReactBundleValidator.Validate(Zip, []).Rejection);
        Assert.Equal(
            BundleRejection.Empty,
            ReactBundleValidator.Validate(Zip with { SizeBytes = 0 }, ["a.tsx"]).Rejection);
    }

    [Fact]
    public void AnOversizedBundleIsRefusedWithAPracticalSuggestion()
    {
        var result = ReactBundleValidator.Validate(
            Zip with { SizeBytes = ReactBundleValidator.MaxSizeBytes + 1 }, ["a.tsx"]);

        Assert.Equal(BundleRejection.TooLarge, result.Rejection);
        Assert.Contains("pasta das telas", result.BusinessMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryRejectionMessageIsFreeOfTechnicalJargon()
    {
        string[] jargon = ["entry", "manifest", "payload", "mime", "stream", "parse"];
        BundleValidation[] rejections =
        [
            ReactBundleValidator.Validate(Zip, []),
            ReactBundleValidator.Validate(Zip, ["leia.txt"]),
            ReactBundleValidator.Validate(Zip with { SizeBytes = long.MaxValue }, ["a.tsx"]),
            ReactBundleValidator.Validate(Zip with { FileName = "x.tar" }, ["a.tsx"])
        ];

        Assert.All(rejections, rejection => Assert.All(jargon, word =>
            Assert.DoesNotContain(word, rejection.BusinessMessage, StringComparison.OrdinalIgnoreCase)));
    }
}

public sealed class PrototypingStagePolicyTests
{
    [Fact]
    public void AProjectWithoutScreensHasNoPrototypingStageAtAll()
    {
        var verdict = PrototypingStagePolicy.Evaluate(
            hasFrontend: false, new OrganizationDesignAssets(false, false, false), false);

        // Marcá-la como satisfeita mentiria sobre um trabalho que nunca precisou ser feito.
        Assert.Equal(PrototypingStageState.NotApplicable, verdict.State);
        Assert.False(verdict.BlocksAdvance);
    }

    /// <summary>Gate da fase: o portão se satisfaz por HERANÇA, e diz que herdou.</summary>
    [Fact]
    public void InheritedIdentityAndDesignSystemSatisfyTheGateWithoutAskingTheOwner()
    {
        var verdict = PrototypingStagePolicy.Evaluate(
            hasFrontend: true, new OrganizationDesignAssets(true, true, false), false);

        Assert.Equal(PrototypingStageState.SatisfiedByInheritance, verdict.State);
        Assert.False(verdict.BlocksAdvance);
        Assert.Contains("já estavam cadastrados", verdict.BusinessMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AnApprovedPrototypeSatisfiesTheGate()
    {
        var verdict = PrototypingStagePolicy.Evaluate(
            hasFrontend: true, new OrganizationDesignAssets(false, false, false), true);

        Assert.Equal(PrototypingStageState.SatisfiedByApproval, verdict.State);
        Assert.False(verdict.BlocksAdvance);
    }

    [Fact]
    public void WithoutIdentityTheGateBlocksAndAsksForIt()
    {
        var verdict = PrototypingStagePolicy.Evaluate(
            hasFrontend: true, new OrganizationDesignAssets(false, false, false), false);

        Assert.True(verdict.BlocksAdvance);
        Assert.Equal(PrototypingStagePolicy.ReasonPendingIdentity, verdict.ReasonCode);
    }

    [Fact]
    public void WithIdentityButNoDesignSystemTheGateAsksForApproval()
    {
        var verdict = PrototypingStagePolicy.Evaluate(
            hasFrontend: true, new OrganizationDesignAssets(true, false, false), false);

        Assert.True(verdict.BlocksAdvance);
        Assert.Equal(PrototypingStagePolicy.ReasonPendingApproval, verdict.ReasonCode);
    }

    /// <summary>A etapa entra ENTRE Planejamento e Desenvolvimento, sem renumerar nada.</summary>
    [Fact]
    public void TheStageIsInsertedBeforeDevelopmentWithoutRenumberingTheOthers()
    {
        var stages = PrototypingStagePolicy.InsertStage(
            ["Descoberta", "Planejamento", "Desenvolvimento", "Revisão", "Entrega"]);

        Assert.Equal(
            ["Descoberta", "Planejamento", "Prototipação", "Desenvolvimento", "Revisão", "Entrega"],
            stages);
    }

    [Fact]
    public void InsertingTwiceIsIdempotent()
    {
        var once = PrototypingStagePolicy.InsertStage(["Planejamento", "Desenvolvimento"]);
        Assert.Same(once, PrototypingStagePolicy.InsertStage(once));
    }

    [Fact]
    public void AVariantWithoutADevelopmentStageIsLeftIntact()
    {
        string[] stages = ["Descoberta", "Entrega"];

        // Anexar ao fim e fingir que a ordem foi respeitada seria pior que não inserir.
        Assert.Equal(stages, PrototypingStagePolicy.InsertStage(stages));
    }
}
