using Harness.Host.V3;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;
using Harness.Persistence.Abstractions.Providers;
using Harness.SharedKernel.Providers;
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
            new FakeOutcome(
                "feito\nPOSEIDON_MISSION_COMPLETE",
                repository =>
                {
                    RunGit(repository, "add", "feature.txt");
                    RunGit(repository, "commit", "-m", "feat: add feature");
                }));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("COMPLETED", result.Execution!.Status);
        Assert.Equal(1, result.Execution.ContinueCount);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Contains("Continue a missão original autonomamente", fake.Calls[1].Prompt.Text);
        Assert.Contains("## ORIGINAL MISSION TEXT", fake.Calls[1].Prompt.Text);
        Assert.Contains(mission.MissionText, fake.Calls[1].Prompt.Text);
        Assert.Equal("VALIDATING", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task CompletionWithDirtyWorktreeQueuesCommitContinuationBeforeCompleting()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(
            new FakeOutcome(
                "feito\nPOSEIDON_MISSION_COMPLETE",
                repository => File.WriteAllText(Path.Combine(repository, "feature.txt"), "uncommitted")),
            new FakeOutcome(
                "commit criado\nPOSEIDON_MISSION_COMPLETE",
                repository =>
                {
                    RunGit(repository, "add", "feature.txt");
                    RunGit(repository, "commit", "-m", "feat: add feature");
                }));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("COMPLETED", result.Execution!.Status);
        Assert.Equal(1, result.Execution.ContinueCount);
        Assert.Equal(1, result.Execution.CommitDelta);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Contains("alterações não commitadas", fake.Calls[1].Prompt.Text);
        Assert.Contains(result.Execution.Events, item => item.Type == "BUILD_COMPLETION_REJECTED");
        Assert.False(V3GitSnapshot.HasUncommittedChanges(repo));
        Assert.Equal("VALIDATING", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task CompletionIgnoresRuntimePoseidonContextPackageWhenCheckingCleanWorktree()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(
            new FakeOutcome(
                "commit criado\nPOSEIDON_MISSION_COMPLETE",
                repository =>
                {
                    Directory.CreateDirectory(Path.Combine(repository, ".poseidon", "context"));
                    File.WriteAllText(Path.Combine(repository, ".poseidon", "context", "INDEX.md"), "runtime context");
                    File.WriteAllText(Path.Combine(repository, "feature.txt"), "committed");
                    RunGit(repository, "add", "feature.txt");
                    RunGit(repository, "commit", "-m", "feat: add feature");
                }));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("COMPLETED", result.Execution!.Status);
        Assert.Equal(1, result.Execution.CommitDelta);
        Assert.Single(fake.Calls);
        Assert.False(V3GitSnapshot.HasUncommittedChanges(repo));
        Assert.Equal("VALIDATING", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task V3RuntimeRecordsUsageUnavailableInvocationWithoutInventingTokens()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(new FakeOutcome("feito\nPOSEIDON_MISSION_COMPLETE"));
        var invocations = new RecordingInvocationStore();
        var (service, _) = Runtime(fake, invocations);
        var (state, mission, accounts) = ArrangeProject(repo);

        var result = await service.DispatchAsync(
            new V3BuildDispatchCommand(state.ProjectId, state, mission, "READY", accounts, "tenant-1"),
            CancellationToken.None);

        Assert.Equal("COMPLETED", result.Execution!.Status);
        var invocation = Assert.Single(invocations.Records);
        Assert.Equal("tenant-1", invocation.TenantId);
        Assert.Equal(state.ProjectId, invocation.ProjectId);
        Assert.Equal(mission.MissionId, invocation.WorkTaskId);
        Assert.Equal(result.Execution.MissionExecutionId, invocation.AttemptId);
        Assert.Equal(0, invocation.InputTokens);
        Assert.Equal(0, invocation.OutputTokens);
        Assert.Contains("usage_unknown", invocation.Outcome, StringComparison.Ordinal);
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
        Assert.Null(fake.Calls[1].ResumeSessionId);
        Assert.Contains("## ORIGINAL MISSION TEXT", fake.Calls[1].Prompt.Text);
        Assert.Contains(mission.MissionText, fake.Calls[1].Prompt.Text);
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
    public async Task StartupRecoveryForValidationKeepsProjectInValidatingForCleanRetry()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(new FakeOutcome("não deveria rodar"));
        var (service, understand) = Runtime(fake);
        var (state, mission, _) = ArrangeProject(repo);
        var validatingState = state with { LifecycleState = "VALIDATING", Status = "BUILD_COMPLETED" };
        var validationMission = mission with { MissionType = "VALIDATE" };
        understand.WriteProject(validatingState);
        understand.WriteMission(validationMission);
        var stale = new V3BuildExecutionRecord(
            "01K00000000000000000000020",
            validationMission.MissionId,
            state.ProjectId,
            "VALIDATE",
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
        Assert.Equal("VALIDATING", understand.ReadProject(state.ProjectId)!.LifecycleState);
        Assert.Equal("RECOVERY_STALLED", understand.ReadProject(state.ProjectId)!.Status);
        Assert.Empty(fake.Calls);
        Assert.Contains(recovered[0].Events, item => item.Type == "BUILD_RECOVERY_STALLED");
    }

    [Fact]
    public async Task RecoveryResumeContinuesOrphanRunningExecution()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(new FakeOutcome("retomado\nPOSEIDON_MISSION_COMPLETE"));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo);
        var orphan = new V3BuildExecutionRecord(
            "01K00000000000000000000019",
            mission.MissionId,
            state.ProjectId,
            "BUILD",
            "worker-a",
            "openai",
            "RUNNING",
            _clock.UtcNow,
            _clock.UtcNow,
            null,
            V3GitSnapshot.Capture(repo).Head,
            V3GitSnapshot.Capture(repo).Head,
            0,
            V3GitSnapshot.Capture(repo).CommitCount,
            "session",
            0,
            0,
            "última saída",
            null,
            null,
            null,
            [],
            []);
        new V3BuildRuntimeStore(_directory).WriteExecution(orphan);

        var resumed = await service.ResumeExecutionAsync(orphan, accounts, "tenant-1", CancellationToken.None);

        Assert.NotNull(resumed.Execution);
        Assert.Equal("COMPLETED", resumed.Execution!.Status);
        Assert.Single(fake.Calls);
        Assert.Null(fake.Calls[0].ResumeSessionId);
        Assert.Contains("Continue a missão original após recuperação", fake.Calls[0].Prompt.Text);
        Assert.Contains("## ORIGINAL MISSION TEXT", fake.Calls[0].Prompt.Text);
        Assert.Contains(mission.MissionText, fake.Calls[0].Prompt.Text);
        Assert.Contains(resumed.Execution.Events, item => item.Type == "BUILD_RECOVERY_RESUMED");
        Assert.Equal("VALIDATING", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task RecoveryResumeFailoversPausedQuotaExecutionWhenAlternateExecutorIsAvailable()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(new FakeOutcome("retomado por executor alternativo\nPOSEIDON_MISSION_COMPLETE"));
        var (service, understand) = Runtime(fake);
        var (state, mission, _) = ArrangeProject(repo, includeBackup: true);
        var paused = new V3BuildExecutionRecord(
            "01K00000000000000000000029",
            mission.MissionId,
            state.ProjectId,
            "BUILD",
            "worker-a",
            "openai",
            "PAUSED_QUOTA",
            _clock.UtcNow,
            _clock.UtcNow,
            null,
            V3GitSnapshot.Capture(repo).Head,
            V3GitSnapshot.Capture(repo).Head,
            0,
            V3GitSnapshot.Capture(repo).CommitCount,
            null,
            0,
            0,
            "quota",
            null,
            null,
            "EXHAUSTED",
            [],
            []);
        new V3BuildRuntimeStore(_directory).WriteExecution(paused);
        var accounts = new[]
        {
            Account("worker-a", priority: 10, state: AgentAccountState.QuotaLimited),
            Account("worker-b", priority: 20),
        };

        var resumed = await service.ResumeExecutionAsync(paused, accounts, "tenant-1", CancellationToken.None);

        Assert.NotNull(resumed.Execution);
        Assert.Equal("COMPLETED", resumed.Execution!.Status);
        Assert.Equal("worker-b", resumed.Execution.ExecutorAccountId);
        Assert.Single(fake.Calls);
        Assert.Equal("worker-b", fake.Calls[0].Account.Alias);
        Assert.Contains(resumed.Execution.Events, item => item.Type == "BUILD_RECOVERY_RESUMED");
        Assert.Equal("VALIDATING", understand.ReadProject(state.ProjectId)!.LifecycleState);
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
    public async Task TransientProviderFailureRetriesSameExecutorBeforeStallPolicy()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(
            new FakeOutcome("oauth request failed: fetch failed ECONNRESET", Status: ExternalAgentRunStatus.Failed, FailureKind: ExternalFailureKind.Transient, FailureCode: "executor.provider_unreachable"),
            new FakeOutcome("retomado\nPOSEIDON_MISSION_COMPLETE"));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("COMPLETED", result.Execution!.Status);
        Assert.Equal("worker-a", result.Execution.ExecutorAccountId);
        Assert.Equal(1, result.Execution.ContinueCount);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Contains(result.Execution.Events, item => item.Type == "BUILD_TRANSIENT_RETRY");
        Assert.DoesNotContain(result.Execution.Events, item => item.Type == "BUILD_STALLED");
        Assert.Equal("VALIDATING", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task PersistentTransientProviderFailureFailsOverWhenAlternateExists()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(
            new FakeOutcome("ECONNRESET", Status: ExternalAgentRunStatus.Failed, FailureKind: ExternalFailureKind.Transient, FailureCode: "executor.provider_unreachable"),
            new FakeOutcome("ECONNRESET", Status: ExternalAgentRunStatus.Failed, FailureKind: ExternalFailureKind.Transient, FailureCode: "executor.provider_unreachable"),
            new FakeOutcome("ECONNRESET", Status: ExternalAgentRunStatus.Failed, FailureKind: ExternalFailureKind.Transient, FailureCode: "executor.provider_unreachable"),
            new FakeOutcome("continuação concluída\nPOSEIDON_MISSION_COMPLETE"));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo, includeBackup: true);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("COMPLETED", result.Execution!.Status);
        Assert.Equal("worker-b", result.Execution.ExecutorAccountId);
        Assert.Equal(3, result.Execution.ContinueCount);
        Assert.Equal(4, fake.Calls.Count);
        Assert.Contains(result.Execution.Continuations, item =>
            item.PreviousExecutor == "worker-a" &&
            item.NewExecutor == "worker-b" &&
            item.Reason == "PROVIDER_TRANSPORT_FAILOVER");
        Assert.Null(fake.Calls[3].ResumeSessionId);
        Assert.Contains("## ORIGINAL MISSION TEXT", fake.Calls[3].Prompt.Text);
        Assert.Contains(mission.MissionText, fake.Calls[3].Prompt.Text);
        Assert.Contains(result.Execution.Events, item => item.Type == "BUILD_TRANSIENT_FAILOVER");
        Assert.DoesNotContain(result.Execution.Events, item => item.Type == "BUILD_STALLED");
        Assert.Equal("VALIDATING", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task PersistentTransientProviderFailureWithoutAlternatePausesProviderNotStalled()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(
            new FakeOutcome("ECONNRESET", Status: ExternalAgentRunStatus.Failed, FailureKind: ExternalFailureKind.Transient, FailureCode: "executor.provider_unreachable"),
            new FakeOutcome("ECONNRESET", Status: ExternalAgentRunStatus.Failed, FailureKind: ExternalFailureKind.Transient, FailureCode: "executor.provider_unreachable"),
            new FakeOutcome("ECONNRESET", Status: ExternalAgentRunStatus.Failed, FailureKind: ExternalFailureKind.Transient, FailureCode: "executor.provider_unreachable"));
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("PAUSED_PROVIDER", result.Execution!.Status);
        Assert.Equal("PROVIDER_TRANSPORT_TRANSIENT", result.Execution.QuotaState);
        Assert.Equal("executor.provider_unreachable", result.Execution.LastFailureCode);
        Assert.Equal(2, result.Execution.ContinueCount);
        Assert.Equal(3, fake.Calls.Count);
        Assert.Contains(result.Execution.Events, item => item.Type == "BUILD_PROVIDER_PAUSED");
        Assert.DoesNotContain(result.Execution.Events, item => item.Type == "BUILD_STALLED");
        var stateAfter = understand.ReadProject(state.ProjectId)!;
        Assert.Equal("BLOCKED", stateAfter.LifecycleState);
        Assert.Equal("PROVIDER_TRANSPORT_TRANSIENT", stateAfter.Status);
    }

    [Fact]
    public async Task TransientProviderFailureHasGlobalBudgetAcrossFailovers()
    {
        var repo = CreateGitRepository();
        var outcomes = Enumerable.Range(0, 12)
            .Select(_ => new FakeOutcome("ECONNRESET", Status: ExternalAgentRunStatus.Failed, FailureKind: ExternalFailureKind.Transient, FailureCode: "executor.provider_unreachable"))
            .ToArray();
        var fake = new FakeBuildExecutor(outcomes);
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo, includeBackup: true);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("PAUSED_PROVIDER", result.Execution!.Status);
        Assert.Equal("PROVIDER_TRANSPORT_TRANSIENT", result.Execution.QuotaState);
        Assert.Equal("executor.provider_unreachable", result.Execution.LastFailureCode);
        Assert.True(result.Execution.ContinueCount <= 8);
        Assert.Equal(8, fake.Calls.Count);
        Assert.Contains(result.Execution.Events, item => item.Type == "BUILD_PROVIDER_PAUSED");
        Assert.Equal("BLOCKED", understand.ReadProject(state.ProjectId)!.LifecycleState);
    }

    [Fact]
    public async Task MixedQuotaAndNoMarkerContinuationsHaveGlobalBudget()
    {
        var repo = CreateGitRepository();
        var outcomes = Enumerable.Range(0, 12)
            .Select(index => index % 2 == 0
                ? new FakeOutcome("quota", FailureKind: ExternalFailureKind.QuotaExhausted, FailureCode: "executor.quota_exhausted")
                : new FakeOutcome("checkpoint sem marcador"))
            .ToArray();
        var fake = new FakeBuildExecutor(outcomes);
        var (service, understand) = Runtime(fake);
        var (state, mission, accounts) = ArrangeProject(repo, includeBackup: true);

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("STALLED", result.Execution!.Status);
        Assert.Equal("continuation_budget_exhausted", result.Execution.LastFailureCode);
        Assert.Equal(8, result.Execution.ContinueCount);
        Assert.Equal(8, fake.Calls.Count);
        Assert.Contains(result.Execution.Events, item =>
            item.Type == "BUILD_STALLED" &&
            item.Detail.Contains("bounded continuation budget", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("BLOCKED", understand.ReadProject(state.ProjectId)!.LifecycleState);
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
    public async Task ValidationContractV2RejectsSummaryOnlyEvidence()
    {
        var repo = CreateGitRepository();
        var fake = new FakeBuildExecutor(new FakeOutcome("""
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
        mission = mission with { MissionContractVersion = V3MissionCompiler.ValidationMissionContractVersion };

        var result = await service.DispatchAsync(Command(state, mission, accounts), CancellationToken.None);

        Assert.NotNull(result.Execution);
        Assert.Equal("BLOCKED", result.Execution!.Status);
        Assert.Equal("validation_evidence_rejected", result.Execution.LastFailureCode);
        Assert.Contains(result.Execution.Events, item =>
            item.Type == "VALIDATION_EVIDENCE_REJECTED" &&
            item.Detail.Contains("validation_manifest_missing", StringComparison.Ordinal));
        Assert.Equal("VALIDATING", understand.ReadProject(state.ProjectId)!.LifecycleState);
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
        Assert.Contains("docs/product/qa-standards.md", mission.MissionText);
    }

    private (V3BuildRuntimeService Service, V3UnderstandStore Understand) Runtime(
        FakeBuildExecutor fake,
        IModelInvocationStore? invocations = null)
    {
        var build = new V3BuildRuntimeStore(_directory);
        var understand = new V3UnderstandStore(_directory);
        return (new V3BuildRuntimeService(build, understand, fake, _clock, invocations), understand);
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
        public List<(AgentAccountContract Account, V3BuildExecutionPrompt Prompt, string Repository, string? ResumeSessionId)> Calls { get; } = [];

        public Task<V3BuildExecutorOutcome> RunAsync(
            AgentAccountContract account,
            V3BuildExecutionPrompt prompt,
            string repository,
            string? resumeSessionId,
            CancellationToken token)
        {
            Calls.Add((account, prompt, repository, resumeSessionId));
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

    private sealed class RecordingInvocationStore : IModelInvocationStore
    {
        public List<ModelInvocationRecord> Records { get; } = [];

        public Task RecordInvocationAsync(ModelInvocationRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ModelInvocationRecord>> GetTaskInvocationsAsync(
            string tenantId,
            string workTaskId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelInvocationRecord>>(
                Records.Where(record => record.TenantId == tenantId && record.WorkTaskId == workTaskId).ToArray());

        public Task<IReadOnlyList<ModelInvocationRecord>> GetProjectInvocationsAsync(
            string tenantId,
            string projectId,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelInvocationRecord>>(
                Records.Where(record => record.TenantId == tenantId && record.ProjectId == projectId).Take(limit).ToArray());

        public Task<decimal> GetTotalCostAsync(
            string tenantId,
            string? projectId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Records
                .Where(record => record.TenantId == tenantId && (projectId is null || record.ProjectId == projectId))
                .Sum(record => record.EstimatedCostUsd));
    }
}
