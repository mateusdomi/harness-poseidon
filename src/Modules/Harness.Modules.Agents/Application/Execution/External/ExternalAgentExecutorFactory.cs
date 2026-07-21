using Harness.Modules.Agents.Application.Accounts;

namespace Harness.Modules.Agents.Application.Execution.External;

/// <summary>
/// Resolve o adapter externo de um executor (CA-4/CA-5).
///
/// Um executor sem adapter implementado é recusado com <c>executor.adapter_not_implemented</c>
/// — nunca substituído silenciosamente por outro. Kimi Code e Antigravity entram em CA-8;
/// até lá, pedir por eles falha de forma explícita em vez de fingir suporte.
/// </summary>
public sealed class ExternalAgentExecutorFactory(AccountProfileProvisioner profiles)
{
    private readonly AccountProfileProvisioner _profiles =
        profiles ?? throw new ArgumentNullException(nameof(profiles));

    public static bool IsImplemented(string executorId) =>
        executorId is ExecutorCatalog.ClaudeCode or ExecutorCatalog.Glm or ExecutorCatalog.Codex;

    public IExternalAgentExecutor Create(string executorId) => executorId switch
    {
        ExecutorCatalog.ClaudeCode => ClaudeCodeExternalAgentExecutor.ForClaudeCode(_profiles),
        ExecutorCatalog.Glm => ClaudeCodeExternalAgentExecutor.ForGlm(_profiles),
        ExecutorCatalog.Codex => CodexExternalAgentExecutor.Create(_profiles),
        _ => throw new ExternalAgentException("executor.adapter_not_implemented"),
    };
}
