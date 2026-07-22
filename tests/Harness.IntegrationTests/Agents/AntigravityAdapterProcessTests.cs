using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// N3/CA-4 — o adapter Antigravity contra um subprocesso REAL, sem gastar cota nem tocar a
/// rede: um `agy` FALSO (script de shell) reproduz cada saída observada na CLI real. Prova
/// o que o exit code do `agy` NÃO permite provar — porque ele sai 0 mesmo sem autenticar:
///
/// - saída de autenticação exigida → `Failed(executor.authentication_required)`;
/// - negação de permissão headless → `Failed(executor.tool_permission_denied)`;
/// - saída vazia → `Failed(executor.no_output)` (Default-FAIL, nunca "passou por omissão");
/// - JSON de veredito válido → `Completed`, e o `CriticReviewContract` fecha o veredito.
/// </summary>
public sealed class AntigravityAdapterProcessTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"harness-agy-proc-{Guid.NewGuid():N}");

    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), $"harness-agy-proc-ws-{Guid.NewGuid():N}");

    private readonly string _binRoot = Path.Combine(
        Path.GetTempPath(), $"harness-agy-bin-{Guid.NewGuid():N}");

    public AntigravityAdapterProcessTests()
    {
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_binRoot);
    }

    [Fact]
    public async Task AuthenticationRequiredOutputBecomesAFailedRunDespiteExitCodeZero()
    {
        var result = await RunFakeAsync(
            "printf 'Error: authentication required. Run '\\''agy'\\'' to log in, then retry.\\n' >&2\nexit 0\n");

        Assert.Equal(ExternalAgentRunStatus.Failed, result.Status);
        Assert.Equal("executor.authentication_required", result.FailureCode);
    }

    [Fact]
    public async Task HeadlessPermissionDenialBecomesAFailedRun()
    {
        var result = await RunFakeAsync(
            "printf 'jetski: no output produced — a tool required the \"command\" permission.\\n'\nexit 0\n");

        Assert.Equal(ExternalAgentRunStatus.Failed, result.Status);
        Assert.Equal("executor.tool_permission_denied", result.FailureCode);
    }

    [Fact]
    public async Task EmptyOutputIsFailClosedNeverASilentSuccess()
    {
        var result = await RunFakeAsync("exit 0\n");

        Assert.Equal(ExternalAgentRunStatus.Failed, result.Status);
        Assert.Equal("executor.no_output", result.FailureCode);
    }

    [Fact]
    public async Task AValidVerdictJsonIsCompletedAndClosedByTheCriticContract()
    {
        var result = await RunFakeAsync(
            "printf '{\"verdict\":\"fail\",\"summary\":\"suite vermelha\"," +
            "\"findings\":[{\"severity\":\"P1\",\"code\":\"regressao\",\"summary\":\"1 teste quebrou\"}]}\\n'\nexit 0\n");

        Assert.Equal(ExternalAgentRunStatus.Completed, result.Status);
        Assert.Contains("verdict", result.FinalMessage, StringComparison.Ordinal);

        var (verdict, _, findings, _) = Harness.Host.Agents.CriticReviewContract.Parse(result.FinalMessage);
        Assert.Equal(Harness.Host.Agents.CriticVerdict.Fail, verdict);
        Assert.Single(findings);
    }

    private async Task<ExternalAgentRunResult> RunFakeAsync(string scriptBody)
    {
        if (OperatingSystem.IsWindows())
        {
            // O `agy` falso é um script POSIX; a prova roda em macOS/Linux (ambiente do Host).
            return new ExternalAgentRunResult(
                ExecutorCatalog.Antigravity, "worker-antigravity-review", null,
                ExternalAgentRunStatus.Completed, string.Empty, [], null, 0, null, 0);
        }

        var fake = WriteFakeAgy(scriptBody);
        var provisioner = new AccountProfileProvisioner(_root);
        var real = ExecutorCatalog.Find(ExecutorCatalog.Antigravity)!;
        var profile = real with { Command = fake };

        var account = new AgentAccountContract(
            "worker-antigravity-review", "antigravity", ExecutorCatalog.Antigravity,
            "keychain://poseidon/worker-antigravity-review", "confighome://worker-antigravity-review",
            ["critic"], [], AgentAccountState.Available, AgentAccountHealth.Unknown,
            1, 0, null, null, null, null, null, 100);
        var handle = provisioner.Ensure(account, profile, Now);

        var executor = new AntigravityExternalAgentExecutor(profile, provisioner);
        var request = new ExternalAgentRunRequest
        {
            Alias = "worker-antigravity-review",
            Prompt = "avalie o diff",
            WorkingDirectory = _workspace,
            Profile = handle.Layout,
            Access = ExternalAgentAccess.ReadOnly,
            Timeout = TimeSpan.FromSeconds(30),
        };

        await using var session = await executor.StartAsync(request);
        return await session.CollectAsync();
    }

    private string WriteFakeAgy(string scriptBody)
    {
        var path = Path.Combine(_binRoot, $"agy-{Guid.NewGuid():N}");
        File.WriteAllText(path, "#!/bin/sh\n" + scriptBody);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return path;
    }

    public void Dispose()
    {
        foreach (var path in new[] { _root, _workspace, _binRoot })
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
