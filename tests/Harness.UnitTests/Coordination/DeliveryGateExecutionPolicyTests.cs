using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// A metade da camada determinística que EXECUTA o que o card exige (OPS-071).
///
/// O defeito que estes testes fixam não era de nenhum dos dois lados da revisão: o pacote de
/// objetivo exigia "Gates: build, tests", o revisor cobrava a prova de execução — corretamente — e
/// a CLI do ator rodava em sandbox que nega o runtime. Entrega sem outro defeito era reprovada com
/// P0 por não provar o que ninguém podia provar.
///
/// Os testes que sustentam o desenho são
/// <see cref="AFailedGateNeverOverwritesAnEarlierFailure"/> — o envelope não apaga a causa, que é a
/// família de defeito mais repetida desta operação — e
/// <see cref="PlannedButNotExecutedIsNotRunNotPass"/>: ausência de verificação nunca é aprovação.
/// </summary>
public sealed class DeliveryGateExecutionPolicyTests
{
    private const string ZeroDependencyManifest = """
        {
          "name": "produto",
          "type": "module",
          "scripts": { "build": "node tools/build.mjs", "test": "node --test tests/", "lint": "node tools/lint.mjs" }
        }
        """;

    private static LayerResult CleanLayer() => new(
        VerificationLayer.Deterministic, LayerVerdict.Pass, "diagnostics.clean");

    [Theory]
    [InlineData("Gates: build, tests\n", new[] { "build", "test" })]
    [InlineData("Gates: build, lint\n", new[] { "build", "lint" })]
    [InlineData("Gates: testes\n", new[] { "test" })]
    public void RequiredGatesComeFromTheSameLineTheReviewerReads(string body, string[] expected)
    {
        Assert.Equal(expected, DeliveryGateExecutionPolicy.ParseRequiredGates(body));
    }

    [Fact]
    public void AGateThePlatformCannotExecuteIsNotInvented()
    {
        // "a11y" não vira comando: fabricar uma execução para ele seria pior que não ter.
        Assert.Empty(DeliveryGateExecutionPolicy.ParseRequiredGates("Gates: a11y, performance\n"));
    }

    private static IReadOnlyList<DeliveryGateCommand> Declared(params DeliveryManifest[] manifests) =>
        [.. manifests.SelectMany(DeliveryGateExecutionPolicy.DeclarationsFromManifest)];

    [Fact]
    public void CardWithoutGatesIsNotApplicableAndLeavesTheLayerUntouched()
    {
        var plan = DeliveryGateExecutionPolicy.Plan([], []);
        var report = DeliveryGateExecutionPolicy.Consolidate(plan, []);

        Assert.True(report.NotApplicable);

        var layer = CleanLayer();
        Assert.Same(layer, DeliveryGateExecutionPolicy.ApplyTo(layer, report));
    }

    [Fact]
    public void TheCommandComesFromTheDeliveredManifestAndOnlyFromIt()
    {
        var plan = DeliveryGateExecutionPolicy.Plan(
            ["build", "test"],
            Declared(new DeliveryManifest("frontend", ZeroDependencyManifest)));

        Assert.Equal(DeliveryGateExecutionPolicy.ReasonPassed, plan.ReasonCode);
        Assert.Collection(
            plan.Commands,
            command =>
            {
                Assert.Equal("build", command.Gate);
                Assert.Equal("build", command.Target);
                Assert.Equal("frontend", command.RelativeDirectory);
            },
            command =>
            {
                Assert.Equal("test", command.Gate);
                Assert.Equal("test", command.Target);
            });
    }

    [Fact]
    public void TheConventionalScriptPathIsAlsoADeclaration()
    {
        // A primeira entrega de codigo aprovada nesta operacao declarou os gates assim: Python
        // sem dependencia de terceiro e tools/backend/{build,test}.sh, espelhando a convencao do
        // repositorio que o proprio ator estava lendo. Reconhecer so package.json teria tornado a
        // unica entrega aprovada impossivel de verificar.
        Assert.Equal("build", DeliveryGateExecutionPolicy.GateForScriptPath("tools/backend/build.sh"));
        Assert.Equal("test", DeliveryGateExecutionPolicy.GateForScriptPath("tools/backend/test.sh"));
        Assert.Equal("lint", DeliveryGateExecutionPolicy.GateForScriptPath("tools/lint.sh"));

        // Conjunto FECHADO: um .sh qualquer da entrega não vira comando.
        Assert.Null(DeliveryGateExecutionPolicy.GateForScriptPath("tools/backend/deploy.sh"));
        Assert.Null(DeliveryGateExecutionPolicy.GateForScriptPath("scripts/test.sh"));
        Assert.Null(DeliveryGateExecutionPolicy.GateForScriptPath("tools/a/b/test.sh"));
        Assert.Null(DeliveryGateExecutionPolicy.GateForScriptPath("tools/backend/test.py"));
    }

    [Fact]
    public void BothSlicesOfAGateRunBecauseVerifyingOneAndSayingBuildPassedIsAHalfTruth()
    {
        var declarations = new List<DeliveryGateCommand>
        {
            new("test", DeliveryGateKind.ShellScript, "tools/backend/test.sh", string.Empty, false),
            new("test", DeliveryGateKind.NpmScript, "test", "frontend", false),
        };

        var plan = DeliveryGateExecutionPolicy.Plan(["test"], declarations);

        Assert.Equal(DeliveryGateExecutionPolicy.ReasonPassed, plan.ReasonCode);
        Assert.Equal(2, plan.Commands.Count);
        Assert.Equal("bash tools/backend/test.sh", plan.Commands[0].Display);
        Assert.Equal("frontend$ npm run test", plan.Commands[1].Display);
    }

    [Fact]
    public void GateWithoutManifestIsRefusedBeforeAnythingRuns()
    {
        var plan = DeliveryGateExecutionPolicy.Plan(["build"], []);

        Assert.Equal(DeliveryGateExecutionPolicy.ReasonManifestMissing, plan.ReasonCode);
        Assert.Empty(plan.Commands);
    }

    [Fact]
    public void ManifestWithoutTheDeclaredScriptIsRefused()
    {
        var plan = DeliveryGateExecutionPolicy.Plan(
            ["build", "lint"],
            Declared(new DeliveryManifest(string.Empty, """{ "scripts": { "build": "node b.mjs" } }""")));

        Assert.Equal(DeliveryGateExecutionPolicy.ReasonScriptMissing, plan.ReasonCode);
        Assert.Contains("lint", plan.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalDependenciesCannotBeVerifiedWithoutNetworkAndThatIsDeclared()
    {
        // A fronteira é "sem rede". Fingir que o gate rodou seria pior; dizer que não rodou
        // bloqueia, e bloquear é o contrato do OPS-064.
        var plan = DeliveryGateExecutionPolicy.Plan(
            ["test"],
            Declared(new DeliveryManifest(
                string.Empty,
                """{ "scripts": { "test": "vitest run" }, "devDependencies": { "vitest": "^2" } }""")));

        Assert.Equal(DeliveryGateExecutionPolicy.ReasonDependenciesUnavailable, plan.ReasonCode);

        var report = DeliveryGateExecutionPolicy.Consolidate(plan, []);
        var layer = DeliveryGateExecutionPolicy.ApplyTo(CleanLayer(), report);

        Assert.Equal(LayerVerdict.NotRun, layer.Verdict);
        Assert.False(LayeredVerificationPolicy.Evaluate([layer]).Approved);
    }

    [Fact]
    public void PlannedButNotExecutedIsNotRunNotPass()
    {
        var plan = DeliveryGateExecutionPolicy.Plan(
            ["build", "test"], Declared(new DeliveryManifest(string.Empty, ZeroDependencyManifest)));

        // Só um dos dois chegou a rodar — o executor parou no meio (teto total, runtime ausente).
        var report = DeliveryGateExecutionPolicy.Consolidate(
            plan,
            [new DeliveryGateOutcome("build", "npm run build", true, 0, false, "ok")]);

        Assert.Equal(DeliveryGateExecutionPolicy.ReasonRuntimeUnavailable, report.ReasonCode);
        Assert.False(report.AllPassed);
        Assert.Equal(
            LayerVerdict.NotRun,
            DeliveryGateExecutionPolicy.ApplyTo(CleanLayer(), report).Verdict);
    }

    [Fact]
    public void AllGatesGreenApprovesTheLayerAndSaysWhy()
    {
        var plan = DeliveryGateExecutionPolicy.Plan(
            ["build", "test"], Declared(new DeliveryManifest(string.Empty, ZeroDependencyManifest)));
        var report = DeliveryGateExecutionPolicy.Consolidate(
            plan,
            [
                new DeliveryGateOutcome("build", "npm run build", true, 0, false, "ok"),
                new DeliveryGateOutcome("test", "npm run test", true, 0, false, "12 pass"),
            ]);

        Assert.True(report.AllPassed);

        var layer = DeliveryGateExecutionPolicy.ApplyTo(CleanLayer(), report);
        Assert.Equal(LayerVerdict.Pass, layer.Verdict);
        Assert.Equal(DeliveryGateExecutionPolicy.ReasonPassed, layer.ReasonCode);
        Assert.True(LayeredVerificationPolicy.MayOccupyReviewer([layer]));
    }

    [Fact]
    public void ARedGateIsAnObjectiveRejection()
    {
        var plan = DeliveryGateExecutionPolicy.Plan(
            ["test"], Declared(new DeliveryManifest(string.Empty, ZeroDependencyManifest)));
        var report = DeliveryGateExecutionPolicy.Consolidate(
            plan,
            [new DeliveryGateOutcome("test", "npm run test", false, 1, false, "1 failing")]);

        Assert.Equal(DeliveryGateExecutionPolicy.ReasonFailed, report.ReasonCode);
        Assert.Equal(
            LayerVerdict.Fail,
            DeliveryGateExecutionPolicy.ApplyTo(CleanLayer(), report).Verdict);
    }

    [Fact]
    public void AFailedGateNeverOverwritesAnEarlierFailure()
    {
        // "O envelope apaga a causa" já apareceu quatro vezes nesta operação. Um segredo achado
        // continua sendo o motivo registrado mesmo com o gate vermelho por cima.
        var secretFailure = new LayerResult(
            VerificationLayer.Deterministic,
            LayerVerdict.Fail,
            DeliverySecretScanGate.ReasonSecretFound,
            "aws-access-key-id em src/a.ts");

        var plan = DeliveryGateExecutionPolicy.Plan(
            ["test"], Declared(new DeliveryManifest(string.Empty, ZeroDependencyManifest)));
        var report = DeliveryGateExecutionPolicy.Consolidate(
            plan,
            [new DeliveryGateOutcome("test", "npm run test", false, 1, false, "1 failing")]);

        var layer = DeliveryGateExecutionPolicy.ApplyTo(secretFailure, report);

        Assert.Same(secretFailure, layer);
        Assert.Equal(DeliverySecretScanGate.ReasonSecretFound, layer.ReasonCode);
    }

    [Fact]
    public void TheEvidenceTellsTheReviewerWhoExecutedAndWhatCameOut()
    {
        var plan = DeliveryGateExecutionPolicy.Plan(
            ["test"], Declared(new DeliveryManifest(string.Empty, ZeroDependencyManifest)));
        var report = DeliveryGateExecutionPolicy.Consolidate(
            plan,
            [new DeliveryGateOutcome("test", "npm run test", true, 0, false, "# pass 12")]);

        var evidence = DeliveryGateExecutionPolicy.DescribeForReviewer(report);

        Assert.Contains("PLATAFORMA", evidence, StringComparison.Ordinal);
        Assert.Contains("npm run test", evidence, StringComparison.Ordinal);
        Assert.Contains("# pass 12", evidence, StringComparison.Ordinal);
        Assert.Contains("PASSOU", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnyExecutionTheEvidenceSaysSoInsteadOfLookingClean()
    {
        var report = DeliveryGateExecutionPolicy.Consolidate(
            DeliveryGateExecutionPolicy.Plan(["build"], []), []);

        var evidence = DeliveryGateExecutionPolicy.DescribeForReviewer(report);

        Assert.Contains("NENHUM gate", evidence, StringComparison.Ordinal);
        Assert.Contains("não é", evidence, StringComparison.Ordinal);
    }
}
