using Harness.Modules.Tools.Domain;

namespace Harness.Modules.Tools.Application;

public interface IToolExecutor<in TInput, TOutput>
{
    string ToolId { get; }
    Task<TOutput> ExecuteAsync(TInput input, CancellationToken cancellationToken = default);
}

public sealed record PolicyCheckedToolInvocation<TInput>(
    TInput Input,
    ToolPolicyDescriptor Descriptor,
    ToolPolicyContext Context,
    ToolRiskTier InvocationRiskTier,
    CapabilityToken CapabilityToken,
    CapabilityAuthorizationRequest Authorization);

public sealed class ToolPolicyDeniedException(ToolPolicyDecision decision) : Exception(decision.Detail)
{
    public ToolPolicyDecision Decision { get; } = decision;
}

public sealed class CapabilityDeniedException(CapabilityDecision decision) : Exception(decision.Detail)
{
    public CapabilityDecision Decision { get; } = decision;
}

public sealed class PolicyCheckedToolExecutor<TInput, TOutput>(
    IToolExecutor<TInput, TOutput> inner,
    SecurityPolicyEnforcementPoint enforcementPoint)
{
    private readonly IToolExecutor<TInput, TOutput> _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly SecurityPolicyEnforcementPoint _enforcementPoint =
        enforcementPoint ?? throw new ArgumentNullException(nameof(enforcementPoint));

    public async Task<TOutput> ExecuteAsync(
        PolicyCheckedToolInvocation<TInput> invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!string.Equals(invocation.Descriptor.ToolId, _inner.ToolId, StringComparison.Ordinal))
            throw new ArgumentException("The descriptor does not match the typed executor.", nameof(invocation));
        var authorization = await _enforcementPoint.AuthorizeAsync(
            invocation.CapabilityToken,
            invocation.Authorization,
            cancellationToken);
        if (!authorization.Allowed)
            throw new CapabilityDeniedException(authorization);
        var decision = ToolExecutionPolicy.Evaluate(new(
            invocation.Descriptor, invocation.Context, invocation.InvocationRiskTier));
        if (!decision.Allowed) throw new ToolPolicyDeniedException(decision);
        return await _inner.ExecuteAsync(invocation.Input, cancellationToken);
    }
}
