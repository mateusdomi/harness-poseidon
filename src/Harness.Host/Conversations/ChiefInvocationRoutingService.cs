using Harness.Modules.Conversations.Contracts;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Providers;

namespace Harness.Host.Conversations;

public sealed class ChiefInvocationRoutingService(
    IAgentCatalogStore agents,
    IProviderCatalogStore providers)
{
    public async Task<ChiefInvocationSelection> ResolveAsync(
        string tenantId, string chiefAgentId, StartChatTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agent = await agents.GetAgentAsync(tenantId, chiefAgentId, cancellationToken)
            ?? throw new ChiefInvocationSelectionException("The Chief agent does not exist.");
        var definition = await agents.GetDefinitionForTenantAsync(
            tenantId, agent.DefinitionId, cancellationToken)
            ?? throw new ChiefInvocationSelectionException("The Chief definition does not exist.");

        var explicitSelection = request.AccountId is not null || request.ModelId is not null ||
            request.Effort is not null || request.FallbackModelIds is not null;
        var modelId = request.ModelId ?? agent.ModelId ?? definition.DefaultModelId
            ?? throw new ChiefInvocationSelectionException("No model is configured for the Chief.");
        var model = await providers.GetModelAsync(tenantId, modelId, cancellationToken)
            ?? throw new ChiefInvocationSelectionException("The selected model does not exist.");
        if (!model.Enabled || !model.Capabilities.Contains("chat", StringComparer.Ordinal))
            throw new ChiefInvocationSelectionException("The selected model is not enabled for chat.");

        var effort = request.Effort ?? agent.Effort ?? definition.DefaultEffort ?? "medium";
        if (effort is not ("low" or "medium" or "high" or "max"))
            throw new ChiefInvocationSelectionException("The selected effort is invalid.");
        var mapping = (model.EffortMappings ?? []).SingleOrDefault(x => x.Effort == effort)
            ?? throw new ChiefInvocationSelectionException("The selected effort is not mapped for this model.");

        var accountId = request.AccountId ?? agent.AccountId ?? definition.PreferredAccountId;
        AccountRecord? account = accountId is null
            ? (await providers.ListAccountsAsync(tenantId, null, 200, cancellationToken))
                .FirstOrDefault(x => x.ProviderId == model.ProviderId && x.State == "active")
            : await providers.GetAccountAsync(tenantId, accountId, cancellationToken);
        if (account is null || account.State != "active" || account.ProviderId != model.ProviderId)
            throw new ChiefInvocationSelectionException("No active compatible account is available for the selected model.");
        if ((account.Capabilities ?? []).Count > 0 &&
            !(account.Capabilities ?? []).Contains("chat", StringComparer.Ordinal))
            throw new ChiefInvocationSelectionException("The selected account is not enabled for chat.");

        var fallbackIds = request.FallbackModelIds ?? agent.FallbackModelIds ??
            definition.FallbackModelIds ?? [];
        if (fallbackIds.Count > 10 || fallbackIds.Distinct(StringComparer.Ordinal).Count() != fallbackIds.Count ||
            fallbackIds.Contains(modelId, StringComparer.Ordinal))
            throw new ChiefInvocationSelectionException("The fallback model list is invalid.");
        foreach (var fallbackId in fallbackIds)
        {
            var fallback = await providers.GetModelAsync(tenantId, fallbackId, cancellationToken)
                ?? throw new ChiefInvocationSelectionException("A fallback model does not exist.");
            if (!fallback.Enabled || fallback.ProviderId != model.ProviderId ||
                !fallback.Capabilities.Contains("chat", StringComparer.Ordinal) ||
                !(fallback.EffortMappings ?? []).Any(x => x.Effort == effort))
                throw new ChiefInvocationSelectionException("A fallback model is incompatible with this invocation.");
        }

        var estimatedInputTokens = Math.Max(1m, Math.Ceiling(request.Content.Length / 4m));
        const decimal estimatedOutputTokens = 1000m;
        decimal? estimatedCost = model.CostPer1kInputUsd is null || model.CostPer1kOutputUsd is null
            ? null
            : decimal.Round(
                estimatedInputTokens / 1000m * model.CostPer1kInputUsd.Value +
                estimatedOutputTokens / 1000m * model.CostPer1kOutputUsd.Value,
                6, MidpointRounding.AwayFromZero);
        decimal? quotaRemaining = account.QuotaLimitUsd is null
            ? null
            : Math.Max(0m, account.QuotaLimitUsd.Value - account.QuotaUsedUsd);
        if (estimatedCost is not null && quotaRemaining is not null && estimatedCost > quotaRemaining)
            throw new ChiefInvocationSelectionException("The estimated invocation cost exceeds the account quota.");

        var source = explicitSelection ? "explicit" : agent.ModelId is not null ? "agent" : "definition";
        var reason = string.IsNullOrWhiteSpace(request.SelectionReason)
            ? source == "explicit"
                ? "Explicit per-invocation override validated against provider capabilities."
                : $"Automatic Chief selection from {source} defaults and current account health."
            : request.SelectionReason.Trim();
        if (reason.Length > 1000)
            throw new ChiefInvocationSelectionException("The selection reason is too long.");

        return new ChiefInvocationSelection(
            account.Id, model.Id, model.Name, effort, mapping.ProviderValue,
            fallbackIds.ToArray(), source, reason, estimatedCost, quotaRemaining);
    }
}

public sealed class ChiefInvocationSelectionException(string detail) : Exception(detail);
