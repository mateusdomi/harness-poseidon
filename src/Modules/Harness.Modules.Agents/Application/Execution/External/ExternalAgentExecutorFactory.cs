using Harness.Modules.Agents.Application.Accounts;

namespace Harness.Modules.Agents.Application.Execution.External;

/// <summary>
/// Resolve o adapter externo de um executor (CA-4/CA-5).
///
/// Um executor sem adapter implementado é recusado com <c>executor.adapter_not_implemented</c>
/// — nunca substituído silenciosamente por outro. Claude Code, GLM, Codex e Antigravity têm
/// adapter real; Kimi Code entra depois. Pedir por um executor sem adapter falha de forma
/// explícita em vez de fingir suporte.
/// </summary>
public sealed class ExternalAgentExecutorFactory(AccountProfileProvisioner profiles)
{
    private readonly AccountProfileProvisioner _profiles =
        profiles ?? throw new ArgumentNullException(nameof(profiles));

    public static bool IsImplemented(string executorId) =>
        executorId is ExecutorCatalog.ClaudeCode or ExecutorCatalog.Glm or ExecutorCatalog.Codex
            or ExecutorCatalog.Antigravity or ExecutorCatalog.KimiCode;

    public IExternalAgentExecutor Create(string executorId) => executorId switch
    {
        ExecutorCatalog.ClaudeCode => ClaudeCodeExternalAgentExecutor.ForClaudeCode(_profiles),
        ExecutorCatalog.Glm => ClaudeCodeExternalAgentExecutor.ForGlm(_profiles),
        ExecutorCatalog.Codex => CodexExternalAgentExecutor.Create(_profiles),
        ExecutorCatalog.Antigravity => AntigravityExternalAgentExecutor.Create(_profiles),
        ExecutorCatalog.KimiCode => KimiExternalAgentExecutor.Create(_profiles),
        _ => throw new ExternalAgentException("executor.adapter_not_implemented"),
    };
}
