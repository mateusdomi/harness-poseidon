using System.Globalization;
using Harness.Modules.Governance.Context;
using Harness.Modules.Governance.Documentation;

namespace Harness.UnitTests.Governance;

/// <summary>
/// O defeito que estes testes travam: o runtime pedia contexto com `workflow=agent-run`,
/// `phase=execution` e `taskType=&lt;papel da conta&gt;`, enquanto o manifesto declarava o
/// vocabulário do playbook. Os dois conjuntos não se cruzavam, e documento canônico endereçado
/// corretamente segundo o playbook simplesmente nunca chegava a quem executa.
///
/// Cada dimensão é testada isoladamente para que uma regressão diga QUAL seletor quebrou.
/// </summary>
public sealed class ContextSelectorVocabularyTests
{
    [Theory]
    [InlineData("1-Triagem", "triage")]
    [InlineData("3-Arquitetura", "architecture")]
    [InlineData("5-Desenvolvimento", "development")]
    [InlineData("7-Homologação", "homologation")]
    [InlineData("9-Sustentação", "sustentation")]
    public void PlaybookPhaseResolvesToTheManifestSlug(string phase, string slug)
    {
        var aliases = ContextSelectorVocabulary.PhaseAliases(phase);

        Assert.Contains(slug, aliases, StringComparer.Ordinal);
        Assert.Contains(phase, aliases, StringComparer.Ordinal);
    }

    [Fact]
    public void PhaseWithoutOrderPrefixStillResolves()
    {
        // A esteira pode renumerar as fases; o manifesto não deveria quebrar por isso.
        Assert.Contains("development", ContextSelectorVocabulary.PhaseAliases("Desenvolvimento"));
    }

    [Fact]
    public void UnknownPhaseNeverThrowsAndNeverPretendsToBeAPlaybookPhase()
    {
        var aliases = ContextSelectorVocabulary.PhaseAliases("Fase Inventada");

        Assert.Contains("Fase Inventada", aliases, StringComparer.Ordinal);
        Assert.DoesNotContain("development", aliases, StringComparer.Ordinal);
    }

    [Fact]
    public void MissingPhaseIsDeclaredUnspecifiedInsteadOfEmpty()
    {
        Assert.Equal([ContextSelectorVocabulary.UnknownPhase], ContextSelectorVocabulary.PhaseAliases(null));
        Assert.Equal([ContextSelectorVocabulary.UnknownTaskType], ContextSelectorVocabulary.TaskTypeAliases("  "));
    }

    [Fact]
    public void LegacyAgentTaskCardTypeAlsoAnswersToThePlaybookVocabulary()
    {
        // `agent_task` é o default histórico da tabela; no playbook o equivalente é `tarefa`.
        // Sem esta ponte, todo card já persistido ficaria fora dos documentos por tipo.
        var aliases = ContextSelectorVocabulary.TaskTypeAliases("agent_task");

        Assert.Contains("agent_task", aliases, StringComparer.Ordinal);
        Assert.Contains("tarefa", aliases, StringComparer.Ordinal);
    }

    [Fact]
    public void AgentDimensionAcceptsBothTheAccountAliasAndThePersonaKey()
    {
        // O manifesto sempre quis dizer "qual agente"; o pedido carregava o alias da CONTA, que é
        // identidade de quem paga a execução, não de quem a executa.
        var aliases = ContextSelectorVocabulary.AgentAliases("worker-claude-secondary", "chief-orchestrator");

        Assert.Contains("worker-claude-secondary", aliases, StringComparer.Ordinal);
        Assert.Contains("chief-orchestrator", aliases, StringComparer.Ordinal);
    }

    [Fact]
    public void NormalizationIsAccentAndCaseInsensitive()
    {
        Assert.Equal("homologacao", ContextSelectorVocabulary.Normalize("Homologação"));
        Assert.Equal("historia", ContextSelectorVocabulary.Normalize("História"));
    }
}

/// <summary>
/// Prova que cada discriminador do manifesto de fato inclui ou exclui documentos no bundle real.
/// Usa um manifesto sintético em disco: exercitar o canon do repositório amarraria o teste ao
/// conteúdo dele e o quebraria a cada documento novo.
/// </summary>
public sealed class ContextSelectionByDimensionTests : IDisposable
{
    private readonly string _root = CreateFixtureRepository();

    [Fact]
    public void DocumentIsSelectedByWorkflow()
    {
        Assert.Contains("by-workflow", Ids(Request()));
        Assert.DoesNotContain("by-workflow", Ids(Request() with { Workflow = "outro-workflow" }));
    }

    [Fact]
    public void DocumentIsSelectedByPlaybookPhase()
    {
        Assert.Contains("by-phase", Ids(Request()));
        Assert.DoesNotContain("by-phase", Ids(Request() with { Phase = "1-Triagem" }));
    }

    [Fact]
    public void DocumentIsSelectedByCardType()
    {
        Assert.Contains("by-card-type", Ids(Request()));
        Assert.DoesNotContain("by-card-type", Ids(Request() with { TaskType = "documento" }));
    }

    [Fact]
    public void DocumentIsSelectedByAgentRole()
    {
        Assert.Contains("by-role", Ids(Request()));
        Assert.DoesNotContain("by-role", Ids(Request() with { AgentRole = "frontend-specialist" }));
    }

    [Fact]
    public void RoleIsNotASubstituteForCardType()
    {
        // A regressão exata que o defeito produzia: o papel entrava na dimensão de tipo de card.
        // Um pedido cujo TIPO é o papel não pode casar o documento endereçado por tipo.
        var byRoleInTaskType = Request() with { TaskType = "backend-specialist", AgentRole = "" };

        Assert.DoesNotContain("by-card-type", Ids(byRoleInTaskType));
        Assert.DoesNotContain("by-role", Ids(byRoleInTaskType));
    }

    [Fact]
    public void CombinedSelectorsMustAllMatch()
    {
        Assert.Contains("by-combination", Ids(Request()));
        Assert.DoesNotContain("by-combination", Ids(Request() with { AgentRole = "critic" }));
        Assert.DoesNotContain("by-combination", Ids(Request() with { Phase = "4-Planejamento" }));
    }

    [Fact]
    public void MandatoryDocumentIsAlwaysSelectedRegardlessOfDimensions()
    {
        var narrow = Request() with
        {
            Workflow = "x", Phase = "y", TaskType = "z", AgentRole = "w", Paths = ["nowhere/**"],
        };

        Assert.Contains("mandatory-core", Ids(narrow));
    }

    [Fact]
    public void DocumentWithoutTheRolesDimensionIsNeverExcludedByIt()
    {
        // `roles` nasceu depois das 50 entradas existentes. Uma dimensão que o documento não
        // declara não pode passar a excluí-lo — seria uma regressão silenciosa de contexto.
        Assert.Contains("no-roles-declared", Ids(Request() with { AgentRole = "qualquer-papel" }));
    }

    private string[] Ids(ContextBundleRequest request) =>
        new ContextBundleBuilder(_root).Build(request).Documents
            .Select(document => document.DocumentId)
            .ToArray();

    private static ContextBundleRequest Request() => new(
        "tenant", "project", "task", "attempt", "worker-alias", "claude-code", null,
        "playbook-standard", "5-Desenvolvimento", "historia", "medium", ["src/**"], "{}",
        ["criterion"], [], [], ["stop"], 12000, null, null, null, "backend-specialist");

    private static string CreateFixtureRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), $"poseidon-selectors-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "governance", "schemas"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));

        File.Copy(
            Path.Combine(RepositoryRoot(), "governance", "schemas", "manifest.schema.json"),
            Path.Combine(root, "governance", "schemas", "manifest.schema.json"));

        var documents = new (string Id, string Extra)[]
        {
            ("mandatory-core", "  load: always\n  workflows:\n  - '*'\n  phases:\n  - '*'\n  taskTypes:\n  - '*'\n  roles:\n  - '*'\n  pathGlobs:\n  - '**'\n"),
            ("by-workflow", "  load: bundle\n  workflows:\n  - playbook-standard\n  phases:\n  - '*'\n  taskTypes:\n  - '*'\n  roles:\n  - '*'\n  pathGlobs:\n  - '**'\n"),
            ("by-phase", "  load: bundle\n  workflows:\n  - '*'\n  phases:\n  - development\n  taskTypes:\n  - '*'\n  roles:\n  - '*'\n  pathGlobs:\n  - '**'\n"),
            ("by-card-type", "  load: bundle\n  workflows:\n  - '*'\n  phases:\n  - '*'\n  taskTypes:\n  - historia\n  roles:\n  - '*'\n  pathGlobs:\n  - '**'\n"),
            ("by-role", "  load: bundle\n  workflows:\n  - '*'\n  phases:\n  - '*'\n  taskTypes:\n  - '*'\n  roles:\n  - backend-specialist\n  pathGlobs:\n  - '**'\n"),
            ("by-combination", "  load: bundle\n  workflows:\n  - playbook-standard\n  phases:\n  - development\n  taskTypes:\n  - historia\n  roles:\n  - backend-specialist\n  pathGlobs:\n  - '**'\n"),
            ("no-roles-declared", "  load: bundle\n  workflows:\n  - '*'\n  phases:\n  - '*'\n  taskTypes:\n  - '*'\n  pathGlobs:\n  - '**'\n"),
        };

        var manifest = new System.Text.StringBuilder();
        manifest.AppendLine("manifestVersion: 1.0.0");
        manifest.AppendLine("lastGeneratedAt: 2026-08-04T00:00:00.0000000+00:00");
        manifest.AppendLine("tokenBudget: 24000");
        manifest.AppendLine("knownOwners:");
        manifest.AppendLine("- Platform Governance");
        manifest.AppendLine("markdownAllowlist: []");
        manifest.AppendLine("documents:");
        foreach (var (id, extra) in documents)
        {
            var relative = $"docs/{id}.md";
            var content = $"# {id}\n\nConteúdo determinístico do documento {id}.\n";
            File.WriteAllText(Path.Combine(root, "docs", $"{id}.md"), content);
            var checksum = Checksum(Path.Combine(root, "docs", $"{id}.md"));
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
            manifest.Append(extra);
            manifest.AppendLine("  riskTiers:");
            manifest.AppendLine("  - medium");
            manifest.AppendLine("  priority: 500");
            manifest.AppendLine(CultureInfo.InvariantCulture, $"  tokenCost: {GovernanceManifestSynchronizer.EstimateTokens(content)}");
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

        File.WriteAllText(Path.Combine(root, "governance", "manifest.yaml"), manifest.ToString());
        return root;
    }

    private static string Checksum(string path) =>
        "sha256:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

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
