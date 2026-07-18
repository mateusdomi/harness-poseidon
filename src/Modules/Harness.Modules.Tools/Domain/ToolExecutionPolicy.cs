namespace Harness.Modules.Tools.Domain;

public enum ToolRiskTier { Low = 0, Medium = 1, High = 2, Critical = 3 }

public sealed record ToolPolicyDescriptor(
    string ToolId,
    bool Enabled,
    ToolRiskTier MaximumRiskTier,
    string InputSchemaJson,
    string OutputSchemaJson);

public sealed record ToolPolicyContext(
    string PhaseName,
    ToolRiskTier TaskRiskTier,
    IReadOnlySet<string> AllowedToolIds,
    bool SandboxActive,
    bool UnsafeModeAccepted);

public sealed record ToolInvocationPolicyRequest(
    ToolPolicyDescriptor Tool,
    ToolPolicyContext Context,
    ToolRiskTier InvocationRiskTier);

public sealed record ToolPolicyDecision(bool Allowed, string Code, string Detail)
{
    public static ToolPolicyDecision Permit() => new(true, "allowed", "Tool invocation satisfies the active policy.");
    public static ToolPolicyDecision Deny(string code, string detail) => new(false, code, detail);
}

public static class ToolExecutionPolicy
{
    public static ToolPolicyDecision Evaluate(ToolInvocationPolicyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Context.PhaseName))
            return ToolPolicyDecision.Deny("phase_required", "A workflow phase is required for tool execution.");
        if (!request.Tool.Enabled)
            return ToolPolicyDecision.Deny("tool_disabled", "The tool is disabled.");
        if (!request.Context.AllowedToolIds.Contains(request.Tool.ToolId))
            return ToolPolicyDecision.Deny("tool_not_allowlisted", "The tool is not allowlisted for the active phase.");
        if (request.InvocationRiskTier > request.Tool.MaximumRiskTier)
            return ToolPolicyDecision.Deny("tool_risk_exceeded", "The invocation exceeds the tool risk ceiling.");
        if (request.InvocationRiskTier > request.Context.TaskRiskTier)
            return ToolPolicyDecision.Deny("task_risk_exceeded", "The invocation exceeds the accepted task risk tier.");
        if (request.InvocationRiskTier >= ToolRiskTier.High &&
            !request.Context.SandboxActive && !request.Context.UnsafeModeAccepted)
            return ToolPolicyDecision.Deny("sandbox_required", "High-risk tool execution requires a sandbox or explicit unsafe-mode acceptance.");
        return ToolPolicyDecision.Permit();
    }
}
