using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Infrastructure.Fake;
using Harness.Host.Observability;
using Harness.Modules.Execution.Application.Sandbox;

namespace Harness.Host.Execution;

public enum IsolatedExecutionMode
{
    Disabled,
    Fake,
    Docker,
}

public sealed record IsolatedExecutionSettings
{
    public bool PathScopePolicyEnabled { get; init; } = true;

    /// <summary>
    /// Chaves de definição que exercem o PAPEL de frontend (CA-1). O papel é
    /// provider-agnostic: Codex, Kimi Code ou outro executor autorizado podem exercê-lo.
    /// `frontend-kimi` permanece válida por compatibilidade.
    /// </summary>
    public IReadOnlyList<string> FrontendSpecialistAgentDefinitionKeys { get; init; } =
    [
        "frontend-specialist",
        "frontend-kimi",
        "kimi",
        "kimi-code",
        "frontend-codex",
        "codex-frontend",
    ];

    /// <summary>
    /// Nome histórico da lista acima, mantido para compatibilidade de configuração
    /// (`Harness:IsolatedExecution:KimiAgentDefinitionKeys`). Quando informado, substitui a
    /// lista do papel de frontend. Prefira `FrontendSpecialistAgentDefinitionKeys`.
    /// </summary>
    public IReadOnlyList<string>? KimiAgentDefinitionKeys { get; init; }

    /// <summary>Lista efetiva do papel de frontend, resolvendo o alias histórico.</summary>
    public IReadOnlyList<string> FrontendRoleDefinitionKeys =>
        KimiAgentDefinitionKeys is { Count: > 0 }
            ? KimiAgentDefinitionKeys
            : FrontendSpecialistAgentDefinitionKeys;

    /// <summary>
    /// Modo de isolamento. O padrão é <see cref="IsolatedExecutionMode.Docker"/> desde o 0-E, que
    /// tornou o contêiner o CAMINHO ÚNICO de execução.
    ///
    /// Nascia `Disabled`, e o efeito era o pior possível: numa instalação com Docker rodando e
    /// tudo pronto, a atestação resolvia `unverified:none`, toda execução era recusada com
    /// `sandbox_required` e o card voltava para a fila — vinte e uma vezes seguidas, sem que nada
    /// dissesse por quê. A fábrica parecia trabalhar e não produzia nada.
    ///
    /// Desligar de propósito continua possível (`Disabled`), mas passa a ser uma DECLARAÇÃO de
    /// quem instala, não o estado em que o produto nasce.
    /// </summary>
    public IsolatedExecutionMode Mode { get; init; } = IsolatedExecutionMode.Docker;

    public string? ControlledRoot { get; init; }

    public string AgentImageName { get; init; } = "harness-sandbox-agent:latest";

    public string ProxyImageName { get; init; } = "harness-sandbox-proxy:latest";

    public string ProxyCommand { get; init; } = "proxy";

    public string ContainerExecutable { get; init; } = "codex";

    public string ExecutorId { get; init; } = "codex-cli";

    public decimal CpuLimit { get; init; } = 1.0m;

    public long MemoryBytes { get; init; } = 1L << 30;

    public long WritableDiskBytes { get; init; } = 256L << 20;

    public int PidsLimit { get; init; } = 256;

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

    public IsolatedExecutionOptions ToOptions() => new()
    {
        AgentImageName = AgentImageName,
        ProxyImageName = ProxyImageName,
        ProxyCommand = ProxyCommand,
        ContainerExecutable = ContainerExecutable,
        CpuLimit = CpuLimit,
        MemoryBytes = MemoryBytes,
        WritableDiskBytes = WritableDiskBytes,
        PidsLimit = PidsLimit,
        HeartbeatInterval = HeartbeatInterval,
    };
}

public sealed class FakeSandboxProvider : ISandboxProvider
{
    public Task<ISandboxProcessSession> OpenProcessSessionAsync(
        SandboxProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult<ISandboxProcessSession>(new FakeSession());
    }

    public Task<SandboxRunResult> RunAsync(
        SandboxRunRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The fake sandbox supports process sessions only.");

    public Task<SandboxResourceInventory> DetectResourcesAsync(
        string attemptId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SandboxResourceInventory([], [], [], []));

    public Task CleanupAsync(string attemptId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// O fake atesta uma sandbox EFETIVA porque é isso que ele simula — é o provider usado nos
    /// testes de fluxo isolado. Ele se identifica como `fake`: a auditoria distingue o que foi
    /// simulado do que foi contido de verdade, em vez de as duas coisas virarem "sandbox ativa".
    /// </summary>
    public Task<SandboxAttestation> AttestAsync(
        SandboxAttestationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new SandboxAttestation(
            request.TenantId,
            request.ProjectId,
            request.AttemptId,
            "fake",
            "1",
            $"fake-sandbox:{request.AttemptId}",
            ["/workspace"],
            "denied",
            RootFilesystemReadOnly: true,
            WorktreeIsolated: true,
            EgressRestricted: true,
            ResourceLimitsApplied: true,
            Verified: true,
            "Fake sandbox provider: boundaries are simulated for tests.",
            request.IssuedAt));
    }

    private sealed class FakeSession : ISandboxProcessSession
    {
        public SandboxProcessPlan ProcessPlan { get; } = new(
            "/usr/bin/true",
            [],
            "/workspace",
            RootFilesystemReadOnly: true,
            WorktreeIsolated: true,
            EgressRestricted: true,
            ResourceLimitsApplied: true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class FakeSandboxAgentExecutorFactory : ISandboxAgentExecutorFactory
{
    public IAgentExecutor Create(SandboxProcessPlan plan, StartIsolatedExecutionCommand command) =>
        new InstrumentedAgentExecutor(new FakeAgentExecutor());
}
