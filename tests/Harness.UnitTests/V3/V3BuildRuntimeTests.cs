using Harness.Host.V3;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;
using Harness.SharedKernel.Time;

namespace Harness.UnitTests.V3;

public sealed class V3BuildRuntimeTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"poseidon-v3-build-runtime-{Guid.NewGuid():N}");
    private readonly IncrementingClock _clock = new(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ExitWithoutMarkerWithProgressQueuesDeterministicContinueThenCompletes()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(
            new FakeOutcome("implementei parte; vou continuar", repository => File.WriteAllText(Path.Combine(repository, "feature.txt"), "progress")),
            new FakeOutcome("feito\nPOSEIDON_MISSION_COMPLETE"));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("COMPLETED", result.Execution!.Status);
        Assert.Equal(1, result.Execution.ContinueCount);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Contains("Continue a missão original autonomamente", fake.Calls[1].Prompt.Text);
        Assert.Equal("VALIDATING", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task ExitWithoutMarkerWithoutProgressTwiceStallsAndBlocksLifecycle()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(
            new FakeOutcome("checkpoint sem marcador"),
            new FakeOutcome("outro checkpoint sem marcador"));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("STALLED", result.Execution!.Status);
        Assert.Equal(1, result.Execution.ContinueCount);
        Assert.Equal(2, result.Execution.ConsecutiveNoProgressCount);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal("BLOCKED", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task QuotaFailoverPreservesMissionAndRepositoryOnAlternateExecutor()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(
            new FakeOutcome("quota", FailureKind: ExternalFailureKind.QuotaExhausted, FailureCode: "executor.quota_exhausted"),
            new FakeOutcome("continuação concluída\nPOSEIDON_MISSION_COMPLETE"));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo, includeBackup: true);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("COMPLETED", result.Execution!.Status);
        Assert.Equal(mission.MissionId, result.Execution.MissionId);
        Assert.Equal("worker-b", result.Execution.ExecutorAccountId);
        Assert.Contains(result.Execution.Continuations, item =>
            item.PreviousExecutor == "worker-a" &&
            item.NewExecutor == "worker-b" &&
            item.Reason == "QUOTA_FAILOVER");
        Assert.Equal(repo, mission.Repository);
        Assert.Equal("VALIDATING", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task DispatchUsesRecommendedExecutorWhenEligible()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(new FakeOutcome("feito\nPOSEIDON_MISSION_COMPLETE"));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo, includeBackup: true);
        var recommendedMission = mission with
        {
            RecommendedExecutor = new V3RecommendedExecutor(
                "worker-b",
                "AVAILABLE",
                "chosen by operator for real dogfood."),
        };
        understand.WriteMission(recommendedMission);

        var result = await service.DispatchAsync(Command(state, recommendedMission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("COMPLETED", result.Execution!.Status);
        Assert.Equal("worker-b", result.Execution.ExecutorAccountId);
        Assert.Equal("worker-b", fake.Calls[0].Account.Alias);
        Assert.Equal("VALIDATING", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task ReservedKimiIsSkippedWhenClaudeExecutorIsAvailable()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(new FakeOutcome("feito\nPOSEIDON_MISSION_COMPLETE"));
        var (service, understand) = Runtime(fake);
        var (state, mission, _) = ArrangeProject(repo);
        var accounts = new[]
        {
            Account("worker-kimi-ui", priority: 1, state: AgentAccountState.Disabled, executorId: ExecutorCatalog.KimiCode, provider: "moonshot"),
            Account("worker-claude-secondary", priority: 20, executorId: ExecutorCatalog.ClaudeCode, provider: "anthropic"),
        };

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("COMPLETED", result.Execution!.Status);
        Assert.Equal("worker-claude-secondary", result.Execution.ExecutorAccountId);
        Assert.Equal("worker-claude-secondary", fake.Calls[0].Account.Alias);
        Assert.Equal("VALIDATING", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task StartupRecoveryDoesNotLeaveRunningExecutionGhostWhenNoExecutorExists()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor();
        var (service, understand) = Runtime(fake);
        var (state, mission, _) = ArrangeProject(repo);
        var stale = new V3BuildExecutionRecord(
            "01K00000000000000000000009",
            mission.MissionId,
            state.ProjectId,
            "BUILD",
            "worker-kimi-ui",
            "moonshot",
            "RUNNING",
            _clock.UtcNow,
            _clock.UtcNow,
            null,
            V3GitSnapshot.Capture(repo).Head,
            V3GitSnapshot.Capture(repo).Head,
            V3GitSnapshot.Capture(repo).CommitCount,
            0,
            "session",
            0,
            0,
            "última saída",
            null,
            null,
            null,
            [],
            []);
        new V3BuildRuntimeStore(_directory).WriteExecution(stale);

        var recovered = await service.RecoverRunningExecutionsAsync(
            [Account("worker-kimi-ui", priority: 1, state: AgentAccountState.Disabled, executorId: ExecutorCatalog.KimiCode, provider: "moonshot")],
            CancellationToken.None);

        Assert.Single(recovered);
        Assert.Equal("PAUSED_QUOTA", recovered[0].Status);
        Assert.Equal("NO_EXECUTOR", recovered[0].QuotaState);
        Assert.Equal("PAUSED_QUOTA", understand.ReadProject(state.ProjectId)!.LifecycleState);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task StartupRecoveryDoesNotDispatchExecutorDuringHostBoot()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(new FakeOutcome("não deveria rodar"));
        var (service, understand) = Runtime(fake);
        var (state, mission, _) = ArrangeProject(repo);
        var stale = new V3BuildExecutionRecord(
            "01K00000000000000000000018",
            mission.MissionId,
            state.ProjectId,
            "BUILD",
            "worker-codex-critic",
            "openai",
            "RUNNING",
            _clock.UtcNow,
            _clock.UtcNow,
            null,
            V3GitSnapshot.Capture(repo).Head,
            V3GitSnapshot.Capture(repo).Head,
            V3GitSnapshot.Capture(repo).CommitCount,
            0,
            "session",
            0,
            0,
            "última saída",
            null,
            null,
            null,
            [],
            []);
        new V3BuildRuntimeStore(_directory).WriteExecution(stale);

        var recovered = await service.RecoverRunningExecutionsAsync(
            [Account("worker-codex-critic", priority: 1, executorId: ExecutorCatalog.Codex, provider: "openai")],
            CancellationToken.None);

        Assert.Single(recovered);
        Assert.Equal("STALLED", recovered[0].Status);
        Assert.Equal("BLOCKED", understand.ReadProject(state.ProjectId)!.LifecycleState);
        Assert.Empty(fake.Calls);
        Assert.Contains(recovered[0].Events, item => item.Type == "BUILD_RECOVERY_STALLED");
    }

    [Fact]
    public async Task QuotaWithoutAlternateExecutorPausesProject()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(
            new FakeOutcome("quota", FailureKind: ExternalFailureKind.QuotaExhausted, FailureCode: "executor.quota_exhausted"));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("PAUSED_QUOTA", result.Execution!.Status);
        Assert.Equal("EXHAUSTED", result.Execution.QuotaState);
        Assert.Equal("PAUSED_QUOTA", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task ProviderQuotaDiagnosticPausesInsteadOfAutoContinuingToStall()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(
            new FakeOutcome(
                "API Error: Request rejected (429) · [1113][Insufficient balance or no resource package. Please recharge.]",
                Status: ExternalAgentRunStatus.Failed,
                FailureCode: "executor.exit_code_1"));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("PAUSED_QUOTA", result.Execution!.Status);
        Assert.Equal("EXHAUSTED", result.Execution.QuotaState);
        Assert.Equal(0, result.Execution.ContinueCount);
        Assert.Single(fake.Calls);
        Assert.Equal("PAUSED_QUOTA", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task HumanBlockerBlocksWithoutContinueUntilHumanAnswer()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(
            new FakeOutcome("POSEIDON_HUMAN_BLOCKER\nPreciso da decisão X."),
            new FakeOutcome("resposta aplicada\nPOSEIDON_MISSION_COMPLETE"));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo);

        var blocked = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(blocked.Execution);
        Assert.Equal("BLOCKED", blocked.Execution!.Status);
        Assert.Single(fake.Calls);
        Assert.Equal("BLOCKED", understand.ReadProject(state.ProjectId)!.LifecycleState);

        var resumed = await service.ContinueWithHumanAnswerAsync(
            blocked.Execution,
            "Use a alternativa segura.",
            accounts,
            CancellationToken.None);

        Assert.NotNull(resumed.Execution);
        Assert.Equal("COMPLETED", resumed.Execution!.Status);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Contains("O humano respondeu", fake.Calls[1].Prompt.Text);
        Assert.Equal("VALIDATING", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task ProcessCrashContinuesSameMissionWithoutDeclaringSuccess()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(
            new FakeOutcome("process crashed", Status: ExternalAgentRunStatus.Failed, FailureKind: ExternalFailureKind.Permanent, FailureCode: "executor.crash"),
            new FakeOutcome("retomado\nPOSEIDON_MISSION_COMPLETE"));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("COMPLETED", result.Execution!.Status);
        Assert.Equal(1, result.Execution.ContinueCount);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Contains(result.Execution.Events, item => item.Type == "BUILD_CONTINUED");
        Assert.Equal("VALIDATING", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task ValidationIgnoresBuildCompleteMarkerAndRequiresValidationMarker()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(
            new FakeOutcome("checkpoint\nPOSEIDON_MISSION_COMPLETE"),
            new FakeOutcome("""
                RequirementsChecked: 1
                RequirementsPassed: 1
                RequirementsFailed: 0
                ChecklistTotal: 2
                ChecklistPass: 2
                ChecklistFixed: 0
                ChecklistNA: 0
                ChecklistFail: 0
                BrowserTestsPassed: 1
                BrowserTestsFailed: 0
                BrowserTestsSkipped: 0
                BugsFound: 0
                BugsFixed: 0
                BugsRemaining: 0
                POSEIDON_VALIDATION_COMPLETE
                """));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeValidationProject(repo);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("COMPLETED", result.Execution!.Status);
        Assert.Equal(1, result.Execution.ContinueCount);
        Assert.Equal("READY_FOR_HUMAN_ACCEPTANCE", understand.ReadProject(state.ProjectId)!.LifecycleState);
        Assert.NotNull(result.Execution.ValidationReport);
    }

    [Fact]
    public async Task ValidationCompletionWithBlockingFailuresDoesNotReachHumanAcceptance()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(new FakeOutcome("""
            RequirementsChecked: 1
            RequirementsPassed: 0
            RequirementsFailed: 1
            ChecklistTotal: 2
            ChecklistPass: 1
            ChecklistFixed: 0
            ChecklistNA: 0
            ChecklistFail: 1
            BrowserTestsPassed: 0
            BrowserTestsFailed: 1
            BrowserTestsSkipped: 0
            BugsFound: 1
            BugsFixed: 0
            BugsRemaining: 1
            POSEIDON_VALIDATION_COMPLETE
            """));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeValidationProject(repo);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("BLOCKED", result.Execution!.Status);
        Assert.Equal("validation_failures_remaining", result.Execution.LastFailureCode);
        Assert.Equal("BLOCKED", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public void ValidationMissionIncludesBrowserChecklistAndPrefersBuildExecutor()
    {
        var repo = CreateGitRepository();
        var (state, _, accounts) = ArrangeProject(repo, includeBackup: true);
        var context = new V3ProjectContextResponse(
            state.ProjectId,
            "Smoke",
            "Construir fixture",
            [],
            [],
            [],
            state with { LifecycleState = "VALIDATING" },
            "Produto fixture",
            "Resumo",
            ["Regra"],
            ["Critério"],
            [],
            [],
            state.PrimaryRequirementsCoverage,
            V3RequirementSourceFacts.Empty,
            [],
            new V3EffectiveStackContract("React", ".NET", "none", "Clean", "Playwright", ["test"]),
            state.Deadline,
            repo,
            "runtime",
            "notification",
            V3Capacity.From(accounts, _clock.UtcNow),
            [],
            "VALIDATING");
        var buildExecution = new V3BuildExecutionRecord(
            "01K00000000000000000000088",
            "01K00000000000000000000001",
            state.ProjectId,
            "BUILD",
            "worker-b",
            "openai",
            "COMPLETED",
            _clock.UtcNow,
            _clock.UtcNow,
            _clock.UtcNow,
            V3GitSnapshot.Capture(repo).Head,
            V3GitSnapshot.Capture(repo).Head,
            V3GitSnapshot.Capture(repo).CommitCount,
            0,
            null,
            0,
            0,
            null,
            null,
            null,
            null,
            [],
            []);

        var mission = V3MissionCompiler.CompileValidationMission(
            context,
            buildExecution,
            V3ValidationExecutorPreview.Select(accounts, buildExecution.ExecutorAccountId),
            _clock.UtcNow);

        Assert.Equal("VALIDATE", mission.MissionType);
        Assert.Equal("worker-b", mission.RecommendedExecutor.AccountAlias);
        Assert.Contains("BROWSER-FIRST POLICY", mission.MissionText);
        Assert.Contains("checklist-auto-auditoria-ia.md", mission.MissionText);
        Assert.Contains("docs/product/qa-standards.md#8-toolchain-local-de-navegador", mission.MissionText);
    }

    private (V3BuildRuntimeService Service, V3UnderstandStore Understand) Runtime(FakeBuildExecutor fake)
    {
        var build = new V3BuildRuntimeStore(_directory);
        var understand = new V3UnderstandStore(_directory);
        return (new V3BuildRuntimeService(build, understand, fake, _clock), understand);
    }

    private static V3BuildDispatchCommand Command(
        V3ProjectUnderstandState state,
        V3BuildMissionRecord mission,
        IReadOnlyList<AgentAccountContract> accounts) =>
        new(state.ProjectId, state, mission, "READY", accounts);

    private (V3ProjectUnderstandState State, V3BuildMissionRecord Mission, IReadOnlyList<AgentAccountContract> Accounts) ArrangeProject(
        string repo,
        bool includeBackup = false)
    {
        var projectId = "01K00000000000000000000000";
        var state = V3ProjectUnderstandState.Create(projectId, _clock.UtcNow) with
        {
            Status = "AUTHORIZED",
            LifecycleState = "READY_TO_START",
            Deadline = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
            Repository = repo,
            AuthorizedAt = _clock.UtcNow,
            StartedAt = _clock.UtcNow,
        };
        var mission = new V3BuildMissionRecord(
            "01K00000000000000000000001",
            projectId,
            "BUILD",
            1,
            _clock.UtcNow,
            "Bruna",
            "write-capable project executor",
            "Construa a menor feature possível.",
            [],
            [],
            new V3EffectiveStackContract("React", ".NET", "none", "simple", "unit tests", ["test"]),
            state.Deadline,
            repo,
            "COMPILED",
            new V3RecommendedExecutor("worker-a", "AVAILABLE", "test"))
        {
            PrimaryRequirementsCoverage =
            [
                new V3SourceCoverage("artifact", "requirements.md", "requirements", 1, 1, 100, true, "test", 10, "conteúdo"),
            ],
        };

        var accounts = new List<AgentAccountContract> { Account("worker-a", priority: 10) };
        if (includeBackup)
        {
            accounts.Add(Account("worker-b", priority: 20));
        }

        var understand = new V3UnderstandStore(_directory);
        understand.WriteProject(state);
        understand.WriteMission(mission);
        return (state, mission, accounts);
    }

    private (V3ProjectUnderstandState State, V3BuildMissionRecord Mission, IReadOnlyList<AgentAccountContract> Accounts) ArrangeValidationProject(
        string repo)
    {
        var (state, _, accounts) = ArrangeProject(repo, includeBackup: true);
        var validationState = state with { LifecycleState = "VALIDATING", Status = "BUILD_COMPLETED" };
        var mission = new V3BuildMissionRecord(
            "01K00000000000000000000011",
            validationState.ProjectId,
            "VALIDATE",
            1,
            _clock.UtcNow,
            "Bruna",
            "validator",
            "Valide o produto pelo navegador.",
            [],
            [new V3KnowledgeReference("docs/product/qa-standards.md", "QA")],
            new V3EffectiveStackContract("React", ".NET", "none", "simple", "Playwright", ["test"]),
            validationState.Deadline,
            repo,
            "COMPILED",
            new V3RecommendedExecutor("worker-a", "AVAILABLE", "test"))
        {
            PrimaryRequirementsCoverage =
            [
                new V3SourceCoverage("artifact", "requirements.md", "requirements", 1, 1, 100, true, "test", 10, "conteúdo"),
            ],
        };
        var understand = new V3UnderstandStore(_directory);
        understand.WriteProject(validationState);
        understand.WriteMission(mission);
        return (validationState, mission, accounts);
    }

    private string CreateGitRepository()
    {
        var repo = Path.Combine(_directory, $"repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        RunGit(repo, "init");
        RunGit(repo, "config", "user.email", "poseidon-tests@example.invalid");
        RunGit(repo, "config", "user.name", "Poseidon Tests");
        File.WriteAllText(Path.Combine(repo, "README.md"), "smoke");
        RunGit(repo, "add", "README.md");
        RunGit(repo, "commit", "-m", "initial");
        return repo;
    }

    private static void RunGit(string repo, params string[] args)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(stderr);
        }
    }

    private static AgentAccountContract Account(
        string alias,
        int priority,
        AgentAccountState state = AgentAccountState.Available,
        string executorId = ExecutorCatalog.Codex,
        string provider = "openai") =>
        new(
            alias,
            provider,
            executorId,
            $"keychain://poseidon/{alias}",
            $"confighome://{alias}",
            [AgentRoles.ProjectExecutor],
            ["**"],
            state,
            state == AgentAccountState.Available ? AgentAccountHealth.Healthy : AgentAccountHealth.Unknown,
            1,
            0,
            null,
            null,
            null,
            null,
            null,
            priority);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class IncrementingClock(DateTimeOffset initial) : IClock
    {
        private DateTimeOffset _current = initial;
        public DateTimeOffset UtcNow
        {
            get
            {
                _current = _current.AddSeconds(1);
                return _current;
            }
        }
    }

    private sealed record FakeOutcome(
        string Output,
        Action<string>? BeforeReturn = null,
        ExternalAgentRunStatus Status = ExternalAgentRunStatus.Completed,
        ExternalFailureKind FailureKind = ExternalFailureKind.Unknown,
        string? FailureCode = null);

    private sealed class FakeBuildExecutor(params FakeOutcome[] outcomes) : IV3BuildExecutor
    {
        private readonly Queue<FakeOutcome> _outcomes = new(outcomes);
        public List<(AgentAccountContract Account, V3BuildExecutionPrompt Prompt, string Repository)> Calls { get; } = [];

        public Task<V3BuildExecutorOutcome> RunAsync(
            AgentAccountContract account,
            V3BuildExecutionPrompt prompt,
            string repository,
            string? resumeSessionId,
            CancellationToken token)
        {
            Calls.Add((account, prompt, repository));
            if (_outcomes.Count == 0)
            {
                throw new InvalidOperationException("No fake outcome configured.");
            }

            var outcome = _outcomes.Dequeue();
            outcome.BeforeReturn?.Invoke(repository);
            return Task.FromResult(new V3BuildExecutorOutcome(
                outcome.Status,
                outcome.Output,
                outcome.FailureKind,
                outcome.FailureCode,
                resumeSessionId ?? "session"));
        }
    }
}
