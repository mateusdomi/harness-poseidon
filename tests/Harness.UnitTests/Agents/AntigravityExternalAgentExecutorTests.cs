using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// N3/CA-4: adapter real do Antigravity (`agy`) como executor de primeira classe, com
/// vocação de critic. As provas aqui NÃO gastam cota: asseguram os argumentos exatos
/// (nenhuma flag inventada — todas observadas por probe na CLI 1.1.5), o modo read-only do
/// critic, a entrega do prompt e a detecção de falha pela SAÍDA (o `agy` sai 0 mesmo sem
/// autenticar, então Default-FAIL não pode depender do exit code).
/// </summary>
public sealed class AntigravityExternalAgentExecutorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"harness-agy-{Guid.NewGuid():N}");

    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), $"harness-agy-ws-{Guid.NewGuid():N}");

    public AntigravityExternalAgentExecutorTests() => Directory.CreateDirectory(_workspace);

    private static AgentAccountContract Account(string alias) =>
        new(alias, "antigravity", ExecutorCatalog.Antigravity, $"keychain://poseidon/{alias}",
            $"confighome://{alias}", ["critic"], [],
            AgentAccountState.Available, AgentAccountHealth.Unknown,
            1, 0, null, null, null, null, null, 100);

    private static AccountProfileHandle Provision(AccountProfileProvisioner provisioner, string alias) =>
        provisioner.Ensure(Account(alias), ExecutorCatalog.Find(ExecutorCatalog.Antigravity)!, Now);

    private ExternalAgentRunRequest Request(
        AccountProfileHandle handle,
        ExternalAgentAccess access = ExternalAgentAccess.ReadOnly,
        string? resume = null,
        string? model = null,
        string? effort = null,
        string prompt = "avalie o diff") =>
        new()
        {
            Alias = handle.Layout.Alias,
            Prompt = prompt,
            WorkingDirectory = _workspace,
            Profile = handle.Layout,
            Access = access,
            ResumeSessionId = resume,
            Model = model,
            Effort = effort,
            Timeout = TimeSpan.FromMinutes(30),
        };

    private static List<string> Arguments(
        ProcessExternalAgentExecutor executor, ExternalAgentRunRequest request) =>
        [.. executor.BuildArguments(
            request, new ExternalAgentRunContext("run-test", Path.Combine(Path.GetTempPath(), "last.txt")))];

    [Fact]
    public void TheExecutorIsImplementedAndResolvableFromTheFactory()
    {
        Assert.True(ExternalAgentExecutorFactory.IsImplemented(ExecutorCatalog.Antigravity));

        var provisioner = new AccountProfileProvisioner(_root);
        var executor = new ExternalAgentExecutorFactory(provisioner).Create(ExecutorCatalog.Antigravity);

        Assert.IsType<AntigravityExternalAgentExecutor>(executor);
        Assert.Equal("agy", executor.Profile.Command);
    }

    [Fact]
    public void CriticRunsReadOnlyInPlanModeAndNeverEdits()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var handle = Provision(provisioner, "worker-antigravity-review");
        var executor = AntigravityExternalAgentExecutor.Create(provisioner);

        var arguments = Arguments(executor, Request(handle));

        // `--print` é o ÚLTIMO flag: ele consome o prompt (que a base anexa em seguida) como
        // seu valor. Colocá-lo antes engoliria o flag seguinte como "prompt".
        Assert.Equal("--print", arguments[^1]);
        // `--mode plan` garante ausência de ESCRITA (nunca `accept-edits`); skip-permissions
        // apenas permite LEITURA sem travar o turno headless com "no output produced".
        Assert.Equal("plan", arguments[arguments.IndexOf("--mode") + 1]);
        Assert.DoesNotContain("accept-edits", arguments);
    }

    [Fact]
    public void ActorGetsAcceptEditsAndSkipPermissionsForNonInteractiveWrite()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var handle = Provision(provisioner, "worker-antigravity-review");
        var executor = AntigravityExternalAgentExecutor.Create(provisioner);

        var arguments = Arguments(executor, Request(handle, ExternalAgentAccess.Workspace));

        Assert.Equal("accept-edits", arguments[arguments.IndexOf("--mode") + 1]);
        Assert.Contains("--dangerously-skip-permissions", arguments);
        Assert.DoesNotContain("--sandbox", arguments);
    }

    [Fact]
    public void PrintTimeoutModelEffortAndResumeUseTheRealFlags()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var handle = Provision(provisioner, "worker-antigravity-review");
        var executor = AntigravityExternalAgentExecutor.Create(provisioner);

        var arguments = Arguments(
            executor,
            Request(handle, resume: "conv-01ky", model: "gemini-3-pro", effort: "high"));

        Assert.Equal("1800s", arguments[arguments.IndexOf("--print-timeout") + 1]);
        // Retomada por `--conversation` (flag real), nunca por ref arbitrária.
        Assert.Equal("conv-01ky", arguments[arguments.IndexOf("--conversation") + 1]);
        Assert.Equal("gemini-3-pro", arguments[arguments.IndexOf("--model") + 1]);
        Assert.Equal("high", arguments[arguments.IndexOf("--effort") + 1]);
    }

    [Fact]
    public async Task AnEffortValueTheInstalledCliDoesNotAcceptIsRefused()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var handle = Provision(provisioner, "worker-antigravity-review");
        var executor = AntigravityExternalAgentExecutor.Create(provisioner);

        // `agy --effort` aceita apenas low|medium|high; `xhigh` seria inventar capacidade.
        var exception = await Assert.ThrowsAsync<ExternalAgentException>(
            () => executor.StartAsync(Request(handle, effort: "xhigh")));
        Assert.Equal("executor.effort_unsupported", exception.Code);
    }

    [Fact]
    public void TheBuiltArgumentsNeverCarryThePromptOrASecret()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var handle = Provision(provisioner, "worker-antigravity-review");
        var executor = AntigravityExternalAgentExecutor.Create(provisioner);

        var arguments = Arguments(executor, Request(handle, prompt: "avalie o diff sensível"));

        // BuildArguments (o contrato com a CLI) não inclui o prompt; ele é anexado só na
        // partida do processo, e passa pelo guard de segredo.
        Assert.DoesNotContain(arguments, argument => argument.Contains("avalie", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APromptThatLooksLikeASecretIsRefusedBeforeReachingTheProcessTable()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var handle = Provision(provisioner, "worker-antigravity-review");
        var executor = AntigravityExternalAgentExecutor.Create(provisioner);

        // O prompt vira argv nesta CLI; um token com forma de segredo nunca pode entrar no argv.
        var request = Request(handle, prompt: "use este token sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAA");
        var exception = await Assert.ThrowsAsync<ExternalAgentException>(
            () => executor.StartAsync(request));
        Assert.Equal("executor.secret_in_prompt", exception.Code);
    }

    [Fact]
    public void AuthenticationSentinelSetsFailureKindToAuthenticationRequired()
    {
        // F-10: sem ExternalFailureKind a falha caía na heurística de substring. O adaptador deve
        // declarar a causa real para que o escalonador trate a conta corretamente.
        var parser = new AntigravityExternalAgentExecutor.AntigravityTextParser();

        var failed = Assert.Single(parser.ParseLine(
            "Error: authentication required. Run 'agy' to log in, then retry.").ToArray());

        Assert.Equal("executor.authentication_required", failed.Code);
        Assert.Equal(ExternalFailureKind.AuthenticationRequired, parser.FailureKind);
    }

    [Fact]
    public void ToolPermissionDeniedSetsFailureKindToPermanent()
    {
        var parser = new AntigravityExternalAgentExecutor.AntigravityTextParser();

        var failed = Assert.Single(parser.ParseLine(
            "no output produced — a tool required the write_permission permission").ToArray());

        Assert.Equal("executor.tool_permission_denied", failed.Code);
        Assert.Equal(ExternalFailureKind.Permanent, parser.FailureKind);
    }

    [Fact]
    public void EmptyOutputWithoutSentinelLeavesFailureKindUnknown()
    {
        var parser = new AntigravityExternalAgentExecutor.AntigravityTextParser();

        Assert.Empty(parser.ParseLine("").ToArray());
        parser.Complete();

        Assert.Equal("executor.no_output", parser.FailureCode);
        Assert.Equal(ExternalFailureKind.Unknown, parser.FailureKind);
    }

    [Fact]
    public void NonEmptyOutputEstimatesUsageFromLength()
    {
        // F-26: a CLI não reporta uso; estimamos tokens de saída para não publicar
        // usage_unknown/output_tokens=0 no ledger.
        var parser = new AntigravityExternalAgentExecutor.AntigravityTextParser();
        const string output = "Esta é uma resposta de revisão com vários caracteres.";

        _ = parser.ParseLine(output).ToArray();
        parser.Complete();

        Assert.NotNull(parser.Usage);
        Assert.Null(parser.Usage!.InputTokens);
        Assert.True(parser.Usage.OutputTokens > 0, "output tokens should be estimated");
        Assert.Equal(Math.Max(1, output.Length / 4), parser.Usage.OutputTokens);
        Assert.Equal(ExternalAgentUsagePrecision.Estimated, parser.Usage.Precision);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _root, _workspace })
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
