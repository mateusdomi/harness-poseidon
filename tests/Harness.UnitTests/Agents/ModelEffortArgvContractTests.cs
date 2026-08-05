using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// Onda 0.4 — a prova de que modelo e esforço CHEGAM à CLI, por provedor, no argv exato.
///
/// A cadeia inteira (rota → despacho → comando → adapter) existe para que a tentativa rode com o
/// cérebro e o esforço decididos — e o elo mais fácil de quebrar em silêncio é o último: o
/// adapter que "esquece" a flag e deixa a CLI no default. Cada teste aqui fixa o argv observado
/// da CLI real instalada; quem mudar um adapter sem manter a flag reprova aqui, não no ledger de
/// uma noite perdida.
/// </summary>
public sealed class ModelEffortArgvContractTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"harness-argv-{Guid.NewGuid():N}");

    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), $"harness-argv-ws-{Guid.NewGuid():N}");

    public ModelEffortArgvContractTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // Limpeza de temp não decide teste.
        }
    }

    [Fact]
    public void ClaudeCodeRecebeModelEEffortComoFlagsExplicitas()
    {
        var arguments = Arguments(
            ExecutorCatalog.ClaudeCode, "claude-opus-4", "xhigh",
            provisioner => ClaudeCodeExternalAgentExecutor.ForClaudeCode(provisioner));

        Assert.Equal("claude-opus-4", arguments[arguments.IndexOf("--model") + 1]);
        Assert.Equal("xhigh", arguments[arguments.IndexOf("--effort") + 1]);
    }

    [Fact]
    public void GlmUsaOMesmoBinarioEAsMesmasFlagsDoClaudeCode()
    {
        var arguments = Arguments(
            ExecutorCatalog.Glm, "glm-4.7", "high",
            provisioner => ClaudeCodeExternalAgentExecutor.ForGlm(provisioner));

        Assert.Equal("glm-4.7", arguments[arguments.IndexOf("--model") + 1]);
        Assert.Equal("high", arguments[arguments.IndexOf("--effort") + 1]);
    }

    [Fact]
    public void CodexRecebeModeloPorMEEsforcoPorOverrideDeConfig()
    {
        var arguments = Arguments(
            ExecutorCatalog.Codex, "gpt-5.2-codex", "high",
            provisioner => CodexExternalAgentExecutor.Create(provisioner));

        Assert.Equal("gpt-5.2-codex", arguments[arguments.IndexOf("-m") + 1]);
        // O Codex não tem `--effort`: o controle real é o override de config observado no
        // config.toml desta máquina (CLI 0.146.0). A flag é exata, aspas incluídas.
        var overrideIndex = arguments.IndexOf("-c");
        Assert.True(overrideIndex >= 0, "faltou o override -c no argv do Codex.");
        Assert.Equal("model_reasoning_effort=\"high\"", arguments[overrideIndex + 1]);
    }

    [Fact]
    public void AntigravityRecebeModelEEffortComoFlagsExplicitas()
    {
        var arguments = Arguments(
            ExecutorCatalog.Antigravity, "gemini-3-pro", "high",
            provisioner => AntigravityExternalAgentExecutor.Create(provisioner));

        Assert.Equal("gemini-3-pro", arguments[arguments.IndexOf("--model") + 1]);
        Assert.Equal("high", arguments[arguments.IndexOf("--effort") + 1]);
    }

    [Theory]
    [InlineData(ExecutorCatalog.ClaudeCode)]
    [InlineData(ExecutorCatalog.Glm)]
    [InlineData(ExecutorCatalog.Codex)]
    [InlineData(ExecutorCatalog.Antigravity)]
    public void SemModeloESemEsforcoNenhumaFlagInventadaEntraNoArgv(string executorId)
    {
        var arguments = Arguments(executorId, model: null, effort: null, Create(executorId));

        Assert.DoesNotContain("--model", arguments);
        Assert.DoesNotContain("--effort", arguments);
        Assert.DoesNotContain("-m", arguments);
        Assert.DoesNotContain(
            arguments, argument => argument.StartsWith("model_reasoning_effort", StringComparison.Ordinal));
    }

    /// <summary>
    /// O caso Kimi da missão: conta cujo executor não tem adapter implementado é RECUSA
    /// fail-closed com código tipado — nunca um executor improvisado, nunca texto fabricado.
    /// </summary>
    [Fact]
    public void ExecutorSemAdapterERecusadoFailClosed()
    {
        Assert.False(ExternalAgentExecutorFactory.IsImplemented(ExecutorCatalog.KimiCode));

        var factory = new ExternalAgentExecutorFactory(new AccountProfileProvisioner(_root));
        var exception = Assert.Throws<ExternalAgentException>(
            () => factory.Create(ExecutorCatalog.KimiCode));
        Assert.Equal("executor.adapter_not_implemented", exception.Code);
    }

    private static Func<AccountProfileProvisioner, ProcessExternalAgentExecutor> Create(string executorId) =>
        executorId switch
        {
            ExecutorCatalog.ClaudeCode => provisioner =>
                ClaudeCodeExternalAgentExecutor.ForClaudeCode(provisioner),
            ExecutorCatalog.Glm => provisioner => ClaudeCodeExternalAgentExecutor.ForGlm(provisioner),
            ExecutorCatalog.Codex => provisioner => CodexExternalAgentExecutor.Create(provisioner),
            ExecutorCatalog.Antigravity => provisioner =>
                AntigravityExternalAgentExecutor.Create(provisioner),
            _ => throw new ArgumentOutOfRangeException(nameof(executorId)),
        };

    private List<string> Arguments(
        string executorId,
        string? model,
        string? effort,
        Func<AccountProfileProvisioner, ProcessExternalAgentExecutor> create)
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var alias = $"conta-{executorId}";
        var account = new AgentAccountContract(
            alias, "provider", executorId, $"keychain://poseidon/{alias}", $"confighome://{alias}",
            ["frontend-specialist"], ["frontend/**"],
            AgentAccountState.Available, AgentAccountHealth.Unknown,
            1, 0, null, null, null, null, null, 100);
        var handle = provisioner.Ensure(account, ExecutorCatalog.Find(executorId)!, Now);
        var executor = create(provisioner);
        var request = new ExternalAgentRunRequest
        {
            Alias = alias,
            Prompt = "implemente a fatia",
            WorkingDirectory = _workspace,
            Profile = handle.Layout,
            Access = ExternalAgentAccess.Workspace,
            Model = model,
            Effort = effort,
        };
        return
        [
            .. executor.BuildArguments(
                request,
                new ExternalAgentRunContext(
                    "run-argv", Path.Combine(Path.GetTempPath(), "last-argv.txt"))),
        ];
    }
}
