using System.Security.Cryptography;
using System.Text.Json;
using Harness.Modules.Governance.Documentation;

namespace Harness.UnitTests.Governance;

public sealed class GovernanceDocumentLinterTests
{
    private static readonly JsonSerializerOptions FixtureJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void CanonicalManifestConformsToJsonSchema()
    {
        var repositoryRoot = FindRepositoryRoot();

        var manifest = new GovernanceManifestService(repositoryRoot).LoadAndValidate();

        Assert.Equal("2.0.0", manifest.ManifestVersion);
        Assert.NotEmpty(manifest.Documents);
        Assert.Contains(manifest.Documents, document => document.Id == "governance-core");
    }

    [Fact]
    public void NegativeFixturesProduceExpectedFindings()
    {
        var fixtures = JsonSerializer.Deserialize<List<NegativeFixture>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Governance", "Fixtures", "negative-cases.json")),
            FixtureJsonOptions)!;

        foreach (var fixture in fixtures)
        {
            using var repository = TemporaryGovernanceRepository.Create(fixture.Name);

            var report = new GovernanceDocumentLinter(repository.Path, checkGeneratedDocuments: false)
                .Lint(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));

            Assert.Contains(report.Findings, finding => finding.Code == fixture.ExpectedCode);
        }
    }

    [Fact]
    public void GeneratedAdapterDriftIsRejected()
    {
        using var repository = TemporaryGovernanceRepository.Create("valid");
        repository.AddGeneratorSources();
        var generator = new GovernanceDocumentGenerator(repository.Path);
        generator.Generate();
        File.AppendAllText(System.IO.Path.Combine(repository.Path, "AGENTS.md"), "\nmanual edit\n");

        var findings = generator.Check();

        Assert.Contains(findings, finding => finding.Code == "GOV021" && finding.Path == "AGENTS.md");
    }

    [Fact]
    public void MarkdownCoverageIgnoresClaudeManagedWorktrees()
    {
        using var repository = TemporaryGovernanceRepository.Create("valid");
        var worktree = System.IO.Path.Combine(repository.Path, ".claude", "worktrees", "agent-1");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(System.IO.Path.Combine(worktree, "README.md"), "# Auxiliary worktree\n");

        var report = new GovernanceDocumentLinter(repository.Path, checkGeneratedDocuments: false)
            .Lint(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));

        Assert.DoesNotContain(report.Findings, finding => finding.Code == "GOV019");
    }

    [Fact]
    public void MarkdownCoverageIgnoresGeneratedBrowserTestResults()
    {
        using var repository = TemporaryGovernanceRepository.Create("valid");
        var results = System.IO.Path.Combine(repository.Path, "frontend", "test-results", "run-1");
        Directory.CreateDirectory(results);
        File.WriteAllText(System.IO.Path.Combine(results, "error-context.md"), "# Generated trace\n");

        var report = new GovernanceDocumentLinter(repository.Path, checkGeneratedDocuments: false)
            .Lint(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));

        Assert.DoesNotContain(report.Findings, finding => finding.Code == "GOV019");
    }

    [Fact]
    public void MarkdownCoverageIgnoresIsolatedBrunaVirtualEnvironment()
    {
        using var repository = TemporaryGovernanceRepository.Create("valid");
        var dependencyDocs = System.IO.Path.Combine(repository.Path, ".venv-bruna", "lib", "python3.9", "site-packages", "pkg");
        Directory.CreateDirectory(dependencyDocs);
        File.WriteAllText(System.IO.Path.Combine(dependencyDocs, "README.md"), "# Third-party package docs\n");

        var report = new GovernanceDocumentLinter(repository.Path, checkGeneratedDocuments: false)
            .Lint(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));

        Assert.DoesNotContain(report.Findings, finding => finding.Code == "GOV019");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "Harness.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed record NegativeFixture(string Name, string ExpectedCode);

    private sealed class TemporaryGovernanceRepository : IDisposable
    {
        private TemporaryGovernanceRepository(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryGovernanceRepository Create(string scenario)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"harness-governance-{Guid.NewGuid():N}");
            Directory.CreateDirectory(System.IO.Path.Combine(path, "governance", "schemas"));
            Directory.CreateDirectory(System.IO.Path.Combine(path, "governance", "rules"));
            File.WriteAllText(System.IO.Path.Combine(path, "Harness.sln"), string.Empty);
            File.Copy(
                System.IO.Path.Combine(FindRepositoryRoot(), "governance", "schemas", "manifest.schema.json"),
                System.IO.Path.Combine(path, "governance", "schemas", "manifest.schema.json"));

            var content = scenario switch
            {
                "broken-link" => "# Core\n\n[missing](missing.md)\n",
                "absolute-path" => "# Core\n\nLocal path: /Users/example/private.\n",
                _ => "# Core\n\nPortable governance.\n"
            };
            File.WriteAllText(System.IO.Path.Combine(path, "governance", "core.md"), content);

            var documents = new List<GovernanceDocument>
            {
                CreateDocument("governance-core", "governance/core.md", content)
            };

            switch (scenario)
            {
                case "duplicate-id":
                    const string second = "# Second\n";
                    File.WriteAllText(System.IO.Path.Combine(path, "second.md"), second);
                    documents.Add(CreateDocument("governance-core", "second.md", second));
                    break;
                case "missing-dependency":
                    documents[0].Dependencies.Add("does-not-exist");
                    break;
                case "canonical-conflict":
                    const string competing = "# Competing\n";
                    File.WriteAllText(System.IO.Path.Combine(path, "competing.md"), competing);
                    documents.Add(CreateDocument("competing-core", "competing.md", competing));
                    break;
                case "superseded-loadable":
                    documents[0] = CopyDocument(
                        documents[0],
                        status: DocumentStatus.Superseded,
                        loadPolicy: DocumentLoadPolicy.Bundle);
                    break;
                case "contains-secret-flag":
                    documents[0] = CopyDocument(documents[0], containsSecrets: true);
                    break;
            }

            var manifest = new GovernanceManifest
            {
                ManifestVersion = "1.0.0",
                LastGeneratedAt = "2026-07-20T12:00:00Z",
                TokenBudget = 1000,
                KnownOwners = ["Platform Governance"],
                MarkdownAllowlist = [],
                Documents = documents
            };
            new GovernanceManifestService(path).Save(manifest);
            return new TemporaryGovernanceRepository(path);
        }

        public void AddGeneratorSources()
        {
            var templateDirectory = System.IO.Path.Combine(Path, "governance", "templates");
            Directory.CreateDirectory(templateDirectory);
            File.WriteAllText(
                System.IO.Path.Combine(templateDirectory, "agent-entrypoint.md.tmpl"),
                "<!-- GENERATED source={{source}} version={{version}} checksum={{checksum}} -->\n# {{provider}}\n{{identity}}\n{{minimumRules}}\n{{discovery}}\n");
            foreach (var name in new[]
                     {
                         "authority",
                         "capacity",
                         "chief-constraints",
                         "code-review",
                         "git",
                         "secrets",
                         "testing",
                         "coordination",
                         "cost",
                         "documentation",
                         "security"
                     })
            {
                File.WriteAllText(System.IO.Path.Combine(Path, "governance", "rules", $"{name}.md"), $"# {name}\n");
            }
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }

        private static GovernanceDocument CreateDocument(string id, string path, string content) => new()
        {
            Id = id,
            Path = path,
            Title = id,
            Category = "governance",
            Topic = "repository-governance",
            Authority = DocumentAuthority.Canonical,
            Scope = "repo",
            Audience = [DocumentAudience.Human, DocumentAudience.Agent],
            Providers = ["codex"],
            Agents = ["*"],
            Workflows = ["*"],
            Phases = ["*"],
            TaskTypes = ["*"],
            RiskTiers = ["low"],
            PathGlobs = ["**"],
            LoadPolicy = DocumentLoadPolicy.Always,
            Priority = 1000,
            TokenEstimate = GovernanceManifestSynchronizer.EstimateTokens(content),
            Owner = "Platform Governance",
            Status = DocumentStatus.Active,
            Version = "1.0.0",
            LastVerifiedAt = "2026-07-20T12:00:00Z",
            ReviewDueAt = "2026-10-20T00:00:00Z",
            Supersedes = [],
            Dependencies = [],
            Related = [],
            EnforcedBy = ["test"],
            Checksum = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)))}",
            ContainsSecrets = false,
            Generated = false,
            SourceOfTruth = path
        };

        private static GovernanceDocument CopyDocument(
            GovernanceDocument source,
            DocumentStatus? status = null,
            DocumentLoadPolicy? loadPolicy = null,
            bool? containsSecrets = null) => new()
            {
                Id = source.Id,
                Path = source.Path,
                Title = source.Title,
                Category = source.Category,
                Topic = source.Topic,
                Authority = source.Authority,
                Scope = source.Scope,
                Audience = source.Audience,
                Providers = source.Providers,
                Agents = source.Agents,
                Workflows = source.Workflows,
                Phases = source.Phases,
                TaskTypes = source.TaskTypes,
                RiskTiers = source.RiskTiers,
                PathGlobs = source.PathGlobs,
                LoadPolicy = loadPolicy ?? source.LoadPolicy,
                Priority = source.Priority,
                TokenEstimate = source.TokenEstimate,
                Owner = source.Owner,
                Status = status ?? source.Status,
                Version = source.Version,
                LastVerifiedAt = source.LastVerifiedAt,
                ReviewDueAt = source.ReviewDueAt,
                Supersedes = source.Supersedes,
                Dependencies = source.Dependencies,
                Related = source.Related,
                EnforcedBy = source.EnforcedBy,
                Checksum = source.Checksum,
                ContainsSecrets = containsSecrets ?? source.ContainsSecrets,
                Generated = source.Generated,
                SourceOfTruth = source.SourceOfTruth
            };
    }
}
