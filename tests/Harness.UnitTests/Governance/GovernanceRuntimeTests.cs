using Harness.Modules.Governance.Context;
using Harness.Modules.Governance.Documentation;
using Harness.Modules.Governance.Evaluation;
using Harness.Modules.Governance.Patching;

namespace Harness.UnitTests.Governance;

public sealed class GovernanceRuntimeTests
{
    [Fact]
    public void BundleIsDeterministicScopedAndNeverLoadsHistoricalResearch()
    {
        var root = FindRepositoryRoot();
        var builder = new ContextBundleBuilder(root);
        var request = Request();

        var first = builder.Build(request);
        var second = builder.Build(request);

        Assert.Equal(first.BundleChecksum, second.BundleChecksum);
        Assert.True(second.CacheHits > first.CacheHits);
        Assert.Empty(first.Conflicts);
        Assert.Contains(first.Documents, document => document.DocumentId == "governance-core");
        Assert.DoesNotContain(first.Documents, document => document.DocumentId.StartsWith("research-", StringComparison.Ordinal));
        Assert.DoesNotContain("runtime:acceptance-criteria", first.Truncated);
        Assert.DoesNotContain("runtime:stop-conditions", first.Truncated);
        Assert.DoesNotContain("runtime:budget", first.Truncated);
    }

    [Fact]
    public void EvaluatorIsDefaultFailAndPreventsActorSelfApproval()
    {
        var evaluator = new FreshContextEvaluator(new FreshContextEvaluatorOptions());
        var request = Evaluation() with { EvaluatorAgentId = "actor", ActorAgentId = "actor" };

        var result = evaluator.Evaluate(request, DateTimeOffset.UtcNow);

        Assert.Equal(EvaluationVerdict.Fail, result.Verdict);
        Assert.Contains(result.Findings, finding => finding.RuleId == "actor_self_approval");
        Assert.True(result.ReadOnly);
        Assert.True(result.CleanContext);
    }

    [Fact]
    public void BundleFailureStillProducesAReceiptSafeBlockingFallback()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "missing-governance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fallback = new ContextBundleBuilder(root).BuildOrFallback(Request());

            Assert.Equal("0.0.0", fallback.ManifestVersion);
            Assert.Empty(fallback.Documents);
            Assert.Contains(fallback.Conflicts, conflict => conflict == "bundle_build_failed:GovernanceManifestException");
            Assert.Contains(fallback.Segments, segment => segment.Kind == ContextSegmentKind.AcceptanceCriteria);
            Assert.Contains(fallback.Segments, segment => segment.Kind == ContextSegmentKind.StopCondition);
            Assert.Contains(fallback.Segments, segment => segment.Kind == ContextSegmentKind.Budget);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void EvaluatorPassesCompleteIndependentEvidence()
    {
        var result = new FreshContextEvaluator(new FreshContextEvaluatorOptions())
            .Evaluate(Evaluation(), DateTimeOffset.UtcNow);

        Assert.Equal(EvaluationVerdict.Pass, result.Verdict);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task HashlineRejectsStaleBeforeWriteAndAppliesMatchingPatchAtomically()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "hashline", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "fixture.txt");
        await File.WriteAllTextAsync(path, "v1");
        try
        {
            var sink = new RecordingAuditSink();
            var service = new HashlinePatchService(root, new HashlinePatchOptions(), sink);
            var stale = await service.ApplyAsync(Command("sha256:" + new string('0', 64), "v2"));
            Assert.Equal(HashlinePatchStatus.StaleRejected, stale.Status);
            Assert.Equal("v1", await File.ReadAllTextAsync(path));
            var applied = await service.ApplyAsync(Command(HashlinePatchService.Checksum("v1"), "v2"));
            Assert.Equal(HashlinePatchStatus.Applied, applied.Status);
            Assert.Equal("v2", await File.ReadAllTextAsync(path));
            Assert.Equal(2, sink.Records.Count);
            Assert.DoesNotContain(Directory.EnumerateFiles(root), file => file.EndsWith(".tmp", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HashlineBenchmarkRecordsImprovedStaleRejectionAndNoRegressions()
    {
        var values = HashlinePatchBenchmark.Run(100);
        var current = values.Single(value => value.Strategy == "current-edit");
        var hashline = values.Single(value => value.Strategy == "hashline");

        Assert.True(hashline.StaleRejections > current.StaleRejections);
        Assert.Equal(current.EditSuccesses, hashline.EditSuccesses);
        Assert.True(hashline.Retries < current.Retries);
        Assert.True(hashline.EstimatedTokens < current.EstimatedTokens);
        Assert.True(current.DurationMicroseconds > 0);
        Assert.True(hashline.DurationMicroseconds > 0);
        Assert.True(current.Regressions > hashline.Regressions);
        Assert.Equal(0, hashline.Regressions);
    }

    [Fact]
    public void StaleDetectorCreatesFindingsWithoutMutatingDocuments()
    {
        var root = FindRepositoryRoot();
        var core = Path.Combine(root, "governance", "core.md");
        var before = File.ReadAllText(core);
        var detector = new StaleDocumentDetector(root);
        IReadOnlySet<string> active = new HashSet<string>(StringComparer.Ordinal);

        var findings = detector.Detect(DateTimeOffset.UtcNow, [], active);

        Assert.Contains(findings, finding => finding.Kind == StaleDocumentFindingKind.NeverSelected);
        Assert.Equal(before, File.ReadAllText(core));
    }

    [Fact]
    public void FreshCloneWithMatchingChecksumsNeverReportsSourceChanged()
    {
        // Um clone/pull reescreve o mtime de TODOS os arquivos, mas o conteúdo continua o
        // verificado (checksums do manifest batem). Mtime sozinho não é fato: sinalizar
        // SourceChanged aqui transformaria o catálogo inteiro em falso positivo — foi o "59
        // detecções" visto na homologação. O detector só acusa quando o CONTEÚDO divergiu.
        var repositoryRoot = FindRepositoryRoot();
        var root = Path.Combine(Path.GetTempPath(), $"stale-clone-{Guid.NewGuid():N}");
        try
        {
            CopyDirectory(Path.Combine(repositoryRoot, "governance"), Path.Combine(root, "governance"));
            CopyDirectory(Path.Combine(repositoryRoot, "docs"), Path.Combine(root, "docs"));
            foreach (var adapter in new[] { "AGENTS.md", "CLAUDE.md", "README.md" })
            {
                File.Copy(Path.Combine(repositoryRoot, adapter), Path.Combine(root, adapter));
            }

            var detector = new StaleDocumentDetector(root);
            IReadOnlySet<string> active = new HashSet<string>(StringComparer.Ordinal);

            var findings = detector.Detect(DateTimeOffset.UtcNow, [], active);
            Assert.DoesNotContain(findings, finding =>
                finding.Kind == StaleDocumentFindingKind.SourceChanged);
            Assert.DoesNotContain(findings, finding =>
                finding.Kind == StaleDocumentFindingKind.ChecksumDrift);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StaleDetectorReportsMissingSourceAsTypedFindingInsteadOfThrowing()
    {
        // Em uma instalação self-contained, um documento do manifest cuja fonte não foi
        // empacotada deve produzir um finding tipado em vez de uma exceção.
        var repositoryRoot = FindRepositoryRoot();
        var root = Path.Combine(Path.GetTempPath(), $"stale-missing-{Guid.NewGuid():N}");
        try
        {
            // Só governance/ é copiado; adapters e docs gerados permanecem ausentes.
            CopyDirectory(Path.Combine(repositoryRoot, "governance"), Path.Combine(root, "governance"));
            var detector = new StaleDocumentDetector(root);
            IReadOnlySet<string> active = new HashSet<string>(StringComparer.Ordinal);

            var exception = Record.Exception(() => detector.Detect(DateTimeOffset.UtcNow, [], active));
            Assert.Null(exception);

            var findings = detector.Detect(DateTimeOffset.UtcNow, [], active);
            Assert.Contains(findings, finding =>
                finding.Kind == StaleDocumentFindingKind.SourceMissing &&
                finding.DocumentId == "adapter-agents");
            // O documento ausente não deve gerar checagens de conteúdo (checksum/source-changed).
            Assert.DoesNotContain(findings, finding =>
                finding.DocumentId == "adapter-agents" &&
                (finding.Kind == StaleDocumentFindingKind.ChecksumDrift ||
                 finding.Kind == StaleDocumentFindingKind.SourceChanged));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static ContextBundleRequest Request() => new(
        "tenant", "project", "task", "attempt", "chief", "poseidon", "fake",
        "chief-turn", "execution", "orchestration", "medium", [], "{}",
        ["criterion"], ["read"], [], ["stop on failure"], 12000);

    private static FreshContextEvaluationRequest Evaluation() => new(
        "evaluation", "tenant", "project", "task", "attempt", "actor", "critic",
        "medium", ["criterion"], "diff", ["evidence"], [new("test", true, "log")]);

    private static HashlinePatchCommand Command(string expected, string content) => new(
        "tenant", "project", "turn", "fixture.txt", expected, content, DateTimeOffset.UtcNow);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "governance", "manifest.yaml"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class RecordingAuditSink : IHashlinePatchAuditSink
    {
        public List<HashlinePatchAuditRecord> Records { get; } = [];

        public Task AppendAsync(HashlinePatchAuditRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }
}
