using Harness.Host.V3;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Readiness.Contracts;
using Harness.Persistence.Abstractions.Projects;

namespace Harness.UnitTests.V3;

public sealed class V3FoundationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"poseidon-v3-foundation-{Guid.NewGuid():N}");

    [Fact]
    public void LifecycleStatesAreTheSimplifiedV3Contract()
    {
        Assert.Equal(
            [
                "DRAFT",
                "UNDERSTANDING",
                "AWAITING_INPUT",
                "READY_TO_START",
                "BUILDING",
                "PAUSED_QUOTA",
                "BLOCKED",
                "VALIDATING",
                "READY_FOR_HUMAN_ACCEPTANCE",
                "HUMAN_ACCEPTED",
            ],
            V3Lifecycle.States);

        Assert.Equal(15, V3Lifecycle.Weights["UNDERSTAND"]);
        Assert.Equal(55, V3Lifecycle.Weights["BUILD"]);
        Assert.Equal(25, V3Lifecycle.Weights["VALIDATE"]);
        Assert.Equal(5, V3Lifecycle.Weights["HUMAN_ACCEPTANCE"]);
    }

    [Fact]
    public void CapacityCountsOnlyAuthenticatedWriteCapableSlotsAsExecutionSlots()
    {
        var now = DateTimeOffset.UnixEpoch;
        var response = V3Capacity.From(
            [
                Account("chief-claude-primary", ExecutorCatalog.ClaudeCode, [AgentRoles.ChiefOrchestrator]),
                Account("worker-codex-project", ExecutorCatalog.Codex, [AgentRoles.ProjectExecutor], concurrency: 2),
                Account("worker-claude-fullstack", ExecutorCatalog.ClaudeCode,
                    [AgentRoles.ProjectExecutor, AgentRoles.BackendSpecialist, AgentRoles.FrontendSpecialist], concurrency: 2),
                Account("worker-codex-review", ExecutorCatalog.Codex, [AgentRoles.Critic]),
                Account("worker-codex-disabled", ExecutorCatalog.Codex, [AgentRoles.ProjectExecutor], state: AgentAccountState.Disabled),
            ],
            now);

        Assert.Equal(1, response.ChiefSlots);
        Assert.Equal(4, response.WriteExecutorSlots);
        Assert.Equal(1, response.ReviewValidationSlots);
        Assert.Equal(4, response.EffectiveExecutionSlots);
    }

    [Fact]
    public void AuthInstructionUsesTheIsolatedExecutorConfigHome()
    {
        var layout = new AccountProfileLayout(
            "chief-claude-primary",
            "/tmp/accounts/chief-claude-primary",
            "/tmp/accounts/chief-claude-primary/config",
            "/tmp/accounts/chief-claude-primary/work",
            "/tmp/accounts/chief-claude-primary/sessions",
            "/tmp/accounts/chief-claude-primary/logs",
            "/tmp/accounts/chief-claude-primary/profile.json",
            "/tmp/accounts/chief-claude-primary/profile.lock");

        var instruction = V3AccountAuthInstruction.For(
            Account("chief-claude-primary", ExecutorCatalog.ClaudeCode, [AgentRoles.ChiefOrchestrator]),
            ExecutorCatalog.Find(ExecutorCatalog.ClaudeCode)!,
            layout,
            "/tmp/agent-accounts.json");

        Assert.Contains("CLAUDE_CONFIG_DIR=", instruction.ShellCommand);
        Assert.Contains(layout.ConfigHomePath, instruction.ShellCommand);
        Assert.DoesNotContain("@", instruction.ShellCommand);
        Assert.DoesNotContain("keychain://", instruction.ShellCommand);
    }

    [Fact]
    public void ChiefAssignmentPersistsAsPrimaryPriorityWithoutSecrets()
    {
        var path = Path.Combine(_directory, "agent-accounts.json");
        Directory.CreateDirectory(_directory);

        var backup = new AgentAccountDefinition
        {
            Alias = "chief-claude-backup",
            ProviderKind = "anthropic",
            ExecutorId = ExecutorCatalog.ClaudeCode,
            CredentialRef = "keychain://poseidon/chief-claude-backup",
            AllowedRoles = [AgentRoles.ChiefOrchestrator],
            AllowedPathScopes = [],
            ConcurrencyLimit = 1,
            Priority = 100,
        };
        AgentAccountConfigurationWriter.Upsert(path, backup);

        _ = V3ChiefAssignmentStore.Write(path, "chief-claude-backup");
        var definitions = AgentAccountConfigurationLoader.LoadDefinitions(path);

        Assert.Equal(10_000, definitions.Single(account => account.Alias == "chief-claude-backup").Priority);
        Assert.True(definitions.Single(account => account.Alias == "chief-claude-primary").Priority < 10_000);
        Assert.DoesNotContain("@", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadinessTreatsArtifactsDatabaseAndOperationalNotificationAsApplicableWhenPresent()
    {
        var project = Project(database: "Oracle", repository: _directory) with
        {
            TargetDeadline = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
        };
        Directory.CreateDirectory(_directory);

        var readiness = V3Readiness.For(
            project,
            ReadySnapshot(project.Id),
            [Account("chief-claude-primary", ExecutorCatalog.ClaudeCode, [AgentRoles.ChiefOrchestrator])],
            v3State: null,
            artifactCount: 1,
            effectiveStack: new V3EffectiveStackContract(
                "React + TypeScript + Vite",
                ".NET",
                "Oracle",
                "Clean Architecture",
                "Playwright",
                ["primary requirements"]),
            operationalNotificationConfigured: true);

        Assert.Equal("PASS", Item(readiness, "Artifacts").Status);
        Assert.NotEqual("NOT_APPLICABLE", Item(readiness, "Database").Status);
        Assert.Contains("Oracle", Item(readiness, "Database").EvidenceProvider, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("PASS", Item(readiness, "Notification").Status);
        Assert.Contains("operational", Item(readiness, "Notification").EvidenceProvider, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadinessAcceptsEffectiveStackDerivedFromPrimaryRequirements()
    {
        var project = Project(database: "none", repository: _directory) with
        {
            Technologies = [],
            TargetDeadline = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
        };
        Directory.CreateDirectory(_directory);

        var readiness = V3Readiness.For(
            project,
            ReadySnapshot(project.Id),
            [
                Account("chief-claude-primary", ExecutorCatalog.ClaudeCode, [AgentRoles.ChiefOrchestrator]),
                Account("worker-codex-project", ExecutorCatalog.Codex, [AgentRoles.ProjectExecutor]),
            ],
            artifactCount: 1,
            effectiveStack: new V3EffectiveStackContract(
                "Frontend conforme requisitos",
                "Backend conforme baseline Poseidon",
                "Oracle",
                "Clean Architecture",
                "Playwright",
                ["PRIMARY_REQUIREMENTS:database"]),
            operationalNotificationConfigured: true,
            runtimeReadyOverride: true);

        Assert.Equal("PASS", Item(readiness, "EffectiveStack").Status);
        Assert.Equal("PASS", Item(readiness, "Database").Status);
    }

    [Fact]
    public void ReadinessMarksNotificationNotApplicableOnlyWithExplicitOperationalPolicyReason()
    {
        var project = Project(database: "none", repository: _directory) with
        {
            TargetDeadline = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
        };
        Directory.CreateDirectory(_directory);

        var readiness = V3Readiness.For(
            project,
            ReadySnapshot(project.Id),
            [Account("chief-claude-primary", ExecutorCatalog.ClaudeCode, [AgentRoles.ChiefOrchestrator])],
            artifactCount: 1,
            effectiveStack: new V3EffectiveStackContract(
                "React + TypeScript + Vite",
                ".NET",
                "none",
                "Clean Architecture",
                "Playwright",
                ["primary requirements"]),
            operationalNotificationConfigured: false,
            runtimeReadyOverride: true);

        Assert.Equal("NOT_APPLICABLE", Item(readiness, "Notification").Status);
        Assert.Contains("operational notification is optional", Item(readiness, "Notification").EvidenceProvider, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("NOT_APPLICABLE", Item(readiness, "Database").Status);
    }

    [Fact]
    public void V3ReadinessIgnoresLegacyConfiguredCeilingWhenAllV3ChecksPass()
    {
        var project = Project(database: "none", repository: _directory) with
        {
            TargetDeadline = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
        };
        Directory.CreateDirectory(_directory);

        var readiness = V3Readiness.For(
            project,
            new ProjectReadinessSnapshot(project.Id, ConfigurationState.Configured, [], []),
            [
                Account("chief-claude-primary", ExecutorCatalog.ClaudeCode, [AgentRoles.ChiefOrchestrator]),
                Account("worker-codex-project", ExecutorCatalog.Codex, [AgentRoles.ProjectExecutor]),
            ],
            artifactCount: 1,
            effectiveStack: new V3EffectiveStackContract(
                "React + TypeScript + Vite",
                ".NET",
                "none",
                "Clean Architecture",
                "Playwright",
                ["primary requirements"]),
            operationalNotificationConfigured: false,
            runtimeReadyOverride: true);

        Assert.Equal("READY", readiness.Overall);
        Assert.Equal("Configured", readiness.LegacyReadinessState);
    }

    [Fact]
    public void V3ReadinessRemainsNotReadyWhenARequiredV3CheckIsBlocked()
    {
        var project = Project(database: "none", repository: Path.Combine(_directory, "missing")) with
        {
            TargetDeadline = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
        };

        var readiness = V3Readiness.For(
            project,
            new ProjectReadinessSnapshot(project.Id, ConfigurationState.Ready, [], []),
            [Account("chief-claude-primary", ExecutorCatalog.ClaudeCode, [AgentRoles.ChiefOrchestrator])],
            artifactCount: 1,
            effectiveStack: new V3EffectiveStackContract(
                "React + TypeScript + Vite",
                ".NET",
                "none",
                "Clean Architecture",
                "Playwright",
                ["primary requirements"]),
            operationalNotificationConfigured: false);

        Assert.Equal("NOT_READY", readiness.Overall);
        Assert.Equal("BLOCKED", Item(readiness, "Repository").Status);
    }

    private static AgentAccountContract Account(
        string alias,
        string executorId,
        IReadOnlyList<string> roles,
        AgentAccountState state = AgentAccountState.Available,
        int concurrency = 1) =>
        new(
            alias,
            executorId == ExecutorCatalog.Codex ? "openai" : "anthropic",
            executorId,
            $"keychain://poseidon/{alias}",
            $"confighome://{alias}",
            roles,
            [.. roles.SelectMany(AgentRoles.PathScopesFor).Distinct(StringComparer.Ordinal)],
            state,
            state == AgentAccountState.Available ? AgentAccountHealth.Healthy : AgentAccountHealth.Unknown,
            concurrency,
            0,
            null,
            null,
            null,
            null,
            null,
            100);

    private static V3ReadinessItem Item(V3ProjectReadinessResponse readiness, string category) =>
        readiness.Items.Single(item => item.Category == category);

    private static ProjectReadinessSnapshot ReadySnapshot(string projectId) =>
        new(projectId, ConfigurationState.Ready, [], []);

    private static ProjectRecord Project(string database, string repository) =>
        new(
            "tenant",
            "01K00000000000000000000000",
            "01K00000000000000000000001",
            "V3 Readiness",
            "V3READY",
            "Sistema com requisitos anexados.",
            "active",
            "normal",
            repository,
            "local",
            "develop",
            database.Equals("none", StringComparison.OrdinalIgnoreCase) ? ["React", ".NET"] : ["React", ".NET", database],
            new ProjectBrandRecord(null, null, null, null),
            [],
            1,
            "chief",
            "live",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
