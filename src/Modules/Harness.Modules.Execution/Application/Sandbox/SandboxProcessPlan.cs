namespace Harness.Modules.Execution.Application.Sandbox;

public sealed record SandboxProcessPlan(
    string HostExecutablePath,
    IReadOnlyList<string> ExecutablePrefixArguments,
    string AgentWorkingDirectory,
    bool RootFilesystemReadOnly,
    bool WorktreeIsolated,
    bool EgressRestricted,
    bool ResourceLimitsApplied,
    /// <summary>
    /// Valores das variáveis declaradas no prefixo como <c>--env NOME</c>. Ficam em memória
    /// e são aplicados ao ambiente do processo cliente (o <c>docker</c>) no spawn — nunca
    /// ao argv, nunca a log. Vazio quando a sessão não pediu ambiente.
    /// </summary>
    IReadOnlyDictionary<string, string>? ContainerEnvironment = null,
    /// <summary>O volume de estado gravável dentro do contêiner (sessões, auth hidratada).</summary>
    string ContainerStateDirectory = "/codex-state");
