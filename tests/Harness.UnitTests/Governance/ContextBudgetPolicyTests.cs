using System.Globalization;
using Harness.Modules.Governance.Context;
using Harness.Modules.Governance.Documentation;

namespace Harness.UnitTests.Governance;

/// <summary>
/// O orçamento cortava por ordem alfabética dentro do mesmo tipo de segmento, sem distinguir o que
/// era indispensável. Na prática isso significava que crescer o canon expulsava a regra de
/// segurança do bundle — e a execução seguia, sem que nada acusasse.
///
/// A política agora tem duas faixas: o obrigatório entra inteiro e primeiro; só o resto disputa o
/// que sobra. Quando o obrigatório sozinho não cabe, o bundle é BLOQUEADO com diagnóstico, porque
/// uma execução sem as regras que a governam não é uma execução governada.
/// </summary>
public sealed class ContextBudgetPolicyTests : IDisposable
{
    private const int SmallDocumentTokens = 30;
    private readonly string _root;

    public ContextBudgetPolicyTests()
    {
        _root = CreateFixtureRepository(SmallDocumentTokens);
    }

    [Fact]
    public void OptionalDocumentIsOmittedWhenTheBudgetRunsOut()
    {
        var bundle = Build(budget: 300);

        Assert.DoesNotContain(
            bundle.Segments, segment => segment.SourceId == "optional-huge");
        Assert.Contains("optional-huge", bundle.Truncated);
    }

    [Fact]
    public void MandatoryDocumentNeverDisappearsToMakeRoomForOptionalOnes()
    {
        var bundle = Build(budget: 300);

        // O obrigatório continua no bundle mesmo com o orçamento apertado, e continua no texto
        // renderizado — não basta constar da lista de documentos.
        Assert.Contains(bundle.Segments, segment => segment.SourceId == "mandatory-core");
        Assert.DoesNotContain("mandatory-core", bundle.Truncated);
        Assert.Contains("mandatory-core", bundle.RenderedContext, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncationRecordsTheReasonThePolicyAndTheCost()
    {
        var bundle = Build(budget: 300);

        var truncation = Assert.Single(
            bundle.Truncations, item => item.SourceId == "optional-huge");
        Assert.Equal("budget_exhausted", truncation.Reason);
        Assert.Equal("bundle", truncation.LoadPolicy);
        Assert.True(truncation.EstimatedTokens > 0);
    }

    [Fact]
    public void MandatoryContextThatDoesNotFitBlocksTheBundleInsteadOfShrinkingIt()
    {
        // Orçamento menor que o próprio núcleo obrigatório: em vez de entregar um agente sem as
        // regras que o governam, a montagem falha de forma explícita e diagnosticável.
        var root = CreateFixtureRepository(mandatoryTokens: 4000);
        try
        {
            var bundle = new ContextBundleBuilder(root).Build(new ContextBundleRequest(
                "tenant", "project", "task", "attempt", "worker-alias", "claude-code", null,
                "playbook-standard", "5-Desenvolvimento", "historia", "medium", ["src/**"], "{}",
                ["criterion"], [], [], ["stop"], 1000, null, null, null, "backend-specialist"));

            Assert.Contains(
                bundle.Conflicts,
                conflict => conflict.StartsWith("mandatory_context_exceeds_budget:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RuntimeInvariantsAreNeverTruncated()
    {
        var bundle = Build(budget: 300);

        Assert.DoesNotContain("runtime:acceptance-criteria", bundle.Truncated);
        Assert.DoesNotContain("runtime:stop-conditions", bundle.Truncated);
        Assert.DoesNotContain("runtime:budget", bundle.Truncated);
    }

    private ContextBundle Build(int budget) =>
        new ContextBundleBuilder(_root).Build(new ContextBundleRequest(
            "tenant", "project", "task", "attempt", "worker-alias", "claude-code", null,
            "playbook-standard", "5-Desenvolvimento", "historia", "medium", ["src/**"], "{}",
            ["criterion"], [], [], ["stop"], budget, null, null, null, "backend-specialist"));

    private static string CreateFixtureRepository(int mandatoryTokens)
    {
        var root = Path.Combine(Path.GetTempPath(), $"poseidon-budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "governance", "schemas"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.Copy(
            Path.Combine(RepositoryRoot(), "governance", "schemas", "manifest.schema.json"),
            Path.Combine(root, "governance", "schemas", "manifest.schema.json"));

        var manifest = new System.Text.StringBuilder();
        manifest.AppendLine("manifestVersion: 1.0.0");
        manifest.AppendLine("lastGeneratedAt: 2026-08-04T00:00:00.0000000+00:00");
        manifest.AppendLine("tokenBudget: 24000");
        manifest.AppendLine("knownOwners:");
        manifest.AppendLine("- Platform Governance");
        manifest.AppendLine("markdownAllowlist: []");
        manifest.AppendLine("documents:");

        Append(manifest, root, "mandatory-core", "always", Words(mandatoryTokens));
        Append(manifest, root, "optional-huge", "bundle", Words(4000));

        File.WriteAllText(Path.Combine(root, "governance", "manifest.yaml"), manifest.ToString());
        return root;
    }

    private static string Words(int approximateTokens) =>
        string.Join(' ', Enumerable.Repeat("regra", Math.Max(1, approximateTokens * 2 / 3)));

    private static void Append(
        System.Text.StringBuilder manifest, string root, string id, string load, string body)
    {
        var relative = $"docs/{id}.md";
        var content = $"# {id}\n\n{body}\n";
        File.WriteAllText(Path.Combine(root, "docs", $"{id}.md"), content);
        var checksum = "sha256:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(Path.Combine(root, "docs", $"{id}.md"))));

        manifest.AppendLine(CultureInfo.InvariantCulture, $"- id: {id}");
        manifest.AppendLine(CultureInfo.InvariantCulture, $"  path: {relative}");
        manifest.AppendLine(CultureInfo.InvariantCulture, $"  title: {id}");
        manifest.AppendLine("  category: rule");
        manifest.AppendLine(CultureInfo.InvariantCulture, $"  topic: {id}");
        manifest.AppendLine("  authority: canonical");
        manifest.AppendLine("  scope: fixture");
        manifest.AppendLine("  audience:");
        manifest.AppendLine("  - agent");
        manifest.AppendLine("  providers:");
        manifest.AppendLine("  - '*'");
        manifest.AppendLine("  agents:");
        manifest.AppendLine("  - '*'");
        manifest.AppendLine("  workflows:");
        manifest.AppendLine("  - '*'");
        manifest.AppendLine("  phases:");
        manifest.AppendLine("  - '*'");
        manifest.AppendLine("  taskTypes:");
        manifest.AppendLine("  - '*'");
        manifest.AppendLine("  riskTiers:");
        manifest.AppendLine("  - medium");
        manifest.AppendLine("  pathGlobs:");
        manifest.AppendLine("  - '**'");
        manifest.AppendLine(CultureInfo.InvariantCulture, $"  load: {load}");
        manifest.AppendLine("  priority: 500");
        manifest.AppendLine(
            CultureInfo.InvariantCulture,
            $"  tokenCost: {GovernanceManifestSynchronizer.EstimateTokens(content)}");
        manifest.AppendLine("  owner: Platform Governance");
        manifest.AppendLine("  status: active");
        manifest.AppendLine("  version: 1.0.0");
        manifest.AppendLine("  lastVerifiedAt: 2026-08-04T00:00:00+00:00");
        manifest.AppendLine("  reviewDueAt: 2027-08-04T00:00:00+00:00");
        manifest.AppendLine("  supersedes: []");
        manifest.AppendLine("  dependencies: []");
        manifest.AppendLine("  related: []");
        manifest.AppendLine("  enforcedBy:");
        manifest.AppendLine("  - ci:governance");
        manifest.AppendLine(CultureInfo.InvariantCulture, $"  checksum: {checksum}");
        manifest.AppendLine("  containsSecrets: false");
        manifest.AppendLine("  generated: false");
        manifest.AppendLine(CultureInfo.InvariantCulture, $"  sourceOfTruth: {relative}");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "governance", "manifest.yaml")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
