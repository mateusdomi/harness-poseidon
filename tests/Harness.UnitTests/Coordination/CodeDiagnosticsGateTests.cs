using Harness.Modules.Coordination.Application;
using Harness.SharedKernel.CodeGraph;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// F15 ligando-se à camada (i) da F14: diagnósticos de compilação como gate PRÉ-REVIEW.
///
/// O teste que sustenta o desenho é <see cref="CleanDiagnosticsNeverPromoteALayerThatDidNotRun"/>:
/// o plug só piora o veredito. Se "sem erro de sintaxe" pudesse aprovar a camada determinística, o
/// sistema teria aprendido a chamar de verificado o que ninguém compilou nem testou.
/// </summary>
public sealed class CodeDiagnosticsGateTests
{
    private static readonly CodeGraphDiagnostic SyntaxError = new(
        "CS1002",
        CodeGraphDiagnosticSeverity.Error,
        "; esperado",
        "src/App/Quebrado.cs",
        1);

    private static LayerResult Deterministic(LayerVerdict verdict) =>
        new(VerificationLayer.Deterministic, verdict, "verification.build_and_tests");

    [Fact]
    public void NoErrorDoesNotBlockAndSaysSo()
    {
        var verdict = CodeDiagnosticsGate.Inspect([], CodeGraphDiagnosticScope.SyntaxOnly);

        Assert.False(verdict.Blocks);
        Assert.Equal(0, verdict.ErrorCount);
        Assert.Equal(CodeDiagnosticsGate.ReasonClean, verdict.ReasonCode);
    }

    [Fact]
    public void AWarningIsNotABlocker()
    {
        var warning = new CodeGraphDiagnostic(
            "CS0168", CodeGraphDiagnosticSeverity.Warning, "variável não usada", "src/a.cs", 3);

        Assert.False(
            CodeDiagnosticsGate.Inspect([warning], CodeGraphDiagnosticScope.SyntaxOnly).Blocks);
    }

    [Fact]
    public void ACompilationErrorBlocksAndPointsAtTheFile()
    {
        var verdict = CodeDiagnosticsGate.Inspect(
            [SyntaxError], CodeGraphDiagnosticScope.SyntaxOnly);

        Assert.True(verdict.Blocks);
        Assert.True(verdict.Definitive);
        Assert.Equal(1, verdict.ErrorCount);
        Assert.Equal(CodeDiagnosticsGate.ReasonBlocked, verdict.ReasonCode);
        // Bloquear sem dizer ONDE obriga o revisor a refazer a apuração que a máquina já fez.
        Assert.Contains("src/App/Quebrado.cs", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnErrorTurnsAPassingDeterministicLayerIntoFailure()
    {
        var verdict = CodeDiagnosticsGate.Inspect(
            [SyntaxError], CodeGraphDiagnosticScope.SyntaxOnly);

        var applied = CodeDiagnosticsGate.ApplyTo(Deterministic(LayerVerdict.Pass), verdict);

        Assert.Equal(LayerVerdict.Fail, applied.Verdict);
        Assert.Equal(CodeDiagnosticsGate.ReasonBlocked, applied.ReasonCode);

        // E o efeito prático: o revisor caro não é ocupado por um trabalho que já se sabe que volta.
        Assert.False(LayeredVerificationPolicy.MayOccupyReviewer([applied]));
    }

    [Fact]
    public void CleanDiagnosticsNeverPromoteALayerThatDidNotRun()
    {
        var clean = CodeDiagnosticsGate.Inspect([], CodeGraphDiagnosticScope.SyntaxOnly);

        var notRun = CodeDiagnosticsGate.ApplyTo(Deterministic(LayerVerdict.NotRun), clean);
        var failed = CodeDiagnosticsGate.ApplyTo(Deterministic(LayerVerdict.Fail), clean);

        // Estes diagnósticos são UM insumo da camada, não a camada: build, testes e varredura de
        // segredo continuam sendo os outros. Ausência de erro de sintaxe não responde por eles.
        Assert.Equal(LayerVerdict.NotRun, notRun.Verdict);
        Assert.Equal(LayerVerdict.Fail, failed.Verdict);
    }

    [Fact]
    public void TheGateRefusesToSpeakForAnotherLayer()
    {
        var clean = CodeDiagnosticsGate.Inspect([], CodeGraphDiagnosticScope.SyntaxOnly);
        var intent = new LayerResult(
            VerificationLayer.Intent, LayerVerdict.Pass, "verification.intent_ok");

        Assert.Throws<ArgumentException>(() => CodeDiagnosticsGate.ApplyTo(intent, clean));
    }

    [Fact]
    public void ABlockedDeterministicLayerStopsTheWholeVerification()
    {
        var verdict = CodeDiagnosticsGate.Inspect(
            [SyntaxError], CodeGraphDiagnosticScope.SyntaxOnly);
        var applied = CodeDiagnosticsGate.ApplyTo(Deterministic(LayerVerdict.Pass), verdict);

        var outcome = LayeredVerificationPolicy.Evaluate(
        [
            applied,
            new LayerResult(VerificationLayer.Behavioral, LayerVerdict.Pass, "ok"),
            new LayerResult(VerificationLayer.Intent, LayerVerdict.Pass, "ok")
        ]);

        // Camada superior não compensa inferior — nem quando as duas de cima passaram.
        Assert.False(outcome.Approved);
        Assert.Equal(VerificationLayer.Deterministic, outcome.BlockedAt);
        Assert.Equal("blocker", outcome.Severity);
    }
}
