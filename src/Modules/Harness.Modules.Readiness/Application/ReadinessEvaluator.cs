using Harness.Modules.Readiness.Contracts;

namespace Harness.Modules.Readiness.Application;

/// <summary>Fato de dependência: presente, e — se presente — se é apenas dado simulado.</summary>
public sealed record DependencyFact(bool Present, bool Simulated, string? ResourceId = null)
{
    public static DependencyFact Missing { get; } = new(false, false, null);
}

/// <summary>Fato do Chief: instância presente, saudável, com modelo resolvível e se é simulado.</summary>
public sealed record ChiefFact(
    bool AgentPresent,
    bool Healthy,
    bool ModelResolves,
    bool Simulated,
    string? AgentId = null)
{
    public static ChiefFact Missing { get; } = new(false, false, false, false, null);
}

/// <summary>Entradas puras já coletadas dos stores. A avaliação não acessa IO.</summary>
public sealed record ReadinessInputs(
    bool ProfileReady,
    bool OrganizationReady,
    bool ProjectExists,
    string? ProjectId,
    DependencyFact ProviderAccount,
    DependencyFact Model,
    bool WorkflowBound,
    ChiefFact Chief);

/// <summary>
/// Avaliador puro e determinístico do read model de prontidão (ADR-017). Não persiste
/// estado nem cria autoridade de domínio; mapeia fatos coletados para estados fechados,
/// bloqueadores tipados e próximas ações. Mensagens são códigos localizáveis.
/// </summary>
public static class ReadinessEvaluator
{
    public static ProjectReadinessSnapshot Evaluate(ReadinessInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var steps = new List<ReadinessStepContract>
        {
            Profile(inputs),
            Organization(inputs),
            Project(inputs),
            ProviderAccount(inputs),
            Model(inputs),
            Workflow(inputs),
            ChiefDefinition(inputs),
            AgentPool(inputs),
            Execution(inputs),
        };

        var overall = steps.Select(step => step.State).Aggregate(Weakest);
        var nextActions = steps
            .Where(step => step.State is not ConfigurationState.Ready && step.NextAction is not null)
            .Select(step => step.NextAction!)
            .GroupBy(action => action.Code, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        return new ProjectReadinessSnapshot(inputs.ProjectId, overall, steps, nextActions);
    }

    private static ReadinessStepContract Profile(ReadinessInputs inputs)
    {
        var state = inputs.ProfileReady ? ConfigurationState.Ready : ConfigurationState.Unconfigured;
        return Step(ReadinessStep.ProfileReady, "identity.profile", state,
            blocker: inputs.ProfileReady ? null : new ReadinessBlocker("profile.missing", []),
            nextAction: inputs.ProfileReady ? null : new ReadinessNextAction("profile.create", "/onboarding", null));
    }

    private static ReadinessStepContract Organization(ReadinessInputs inputs)
    {
        var state = inputs.OrganizationReady ? ConfigurationState.Ready : ConfigurationState.Unconfigured;
        return Step(ReadinessStep.OrganizationReady, "organizations", state,
            blocker: inputs.OrganizationReady ? null : new ReadinessBlocker("organization.missing", []),
            nextAction: inputs.OrganizationReady ? null : new ReadinessNextAction("organization.create", "/organizations", null));
    }

    private static ReadinessStepContract Project(ReadinessInputs inputs)
    {
        // Precondição: criar projeto exige organização (S6.1). Sem organização a etapa é
        // bloqueada com a ação de criar organização, nunca um estado "preenchível" vazio.
        if (!inputs.OrganizationReady)
        {
            return Step(ReadinessStep.ProjectReady, "projects", ConfigurationState.Unconfigured,
                blocker: new ReadinessBlocker("organization.required", []),
                nextAction: new ReadinessNextAction("organization.create", "/organizations", null));
        }

        var state = inputs.ProjectExists ? ConfigurationState.Ready : ConfigurationState.Unconfigured;
        return Step(ReadinessStep.ProjectReady, "projects", state,
            relatedIds: inputs.ProjectId is null ? [] : [inputs.ProjectId],
            blocker: inputs.ProjectExists ? null : new ReadinessBlocker("project.missing", []),
            nextAction: inputs.ProjectExists ? null : new ReadinessNextAction("project.create", "/projects", null));
    }

    private static ReadinessStepContract ProviderAccount(ReadinessInputs inputs)
    {
        var fact = inputs.ProviderAccount;
        var state = !fact.Present
            ? ConfigurationState.Unconfigured
            : fact.Simulated ? ConfigurationState.Simulated : ConfigurationState.Configured;
        return Step(ReadinessStep.ProviderAccountReady, "providers.accounts", state,
            relatedIds: fact.ResourceId is null ? [] : [fact.ResourceId],
            blocker: fact.Present ? null : new ReadinessBlocker("provider_account.missing", []),
            nextAction: fact.Present ? null : new ReadinessNextAction("provider.connectAccount", "/providers", null));
    }

    private static ReadinessStepContract Model(ReadinessInputs inputs)
    {
        var fact = inputs.Model;
        var state = !fact.Present
            ? ConfigurationState.Unconfigured
            : fact.Simulated ? ConfigurationState.Simulated : ConfigurationState.Ready;
        return Step(ReadinessStep.ModelReady, "providers.models", state,
            relatedIds: fact.ResourceId is null ? [] : [fact.ResourceId],
            blocker: fact.Present ? null : new ReadinessBlocker("model.none_chat_enabled", []),
            nextAction: fact.Present ? null : new ReadinessNextAction("model.enable", "/providers", null));
    }

    private static ReadinessStepContract Workflow(ReadinessInputs inputs)
    {
        var state = inputs.WorkflowBound ? ConfigurationState.Ready : ConfigurationState.Unconfigured;
        return Step(ReadinessStep.WorkflowReady, "workflows", state,
            blocker: inputs.WorkflowBound ? null : new ReadinessBlocker("workflow.unbound", []),
            nextAction: inputs.WorkflowBound ? null : new ReadinessNextAction("workflow.bind", "/workflows", null));
    }

    private static ReadinessStepContract ChiefDefinition(ReadinessInputs inputs)
    {
        var chief = inputs.Chief;
        if (!chief.AgentPresent)
        {
            return Step(ReadinessStep.ChiefDefinitionReady, "agents.chief", ConfigurationState.Unconfigured,
                blocker: new ReadinessBlocker("chief.missing", []),
                nextAction: new ReadinessNextAction("chief.configureModel", "/agents", chief.AgentId));
        }

        if (!chief.ModelResolves)
        {
            return Step(ReadinessStep.ChiefDefinitionReady, "agents.chief", ConfigurationState.Unconfigured,
                relatedIds: chief.AgentId is null ? [] : [chief.AgentId],
                blocker: new ReadinessBlocker("chief.model_unresolved", []),
                nextAction: new ReadinessNextAction("chief.configureModel", "/agents", chief.AgentId));
        }

        var state = chief.Simulated ? ConfigurationState.Simulated : ConfigurationState.Ready;
        return Step(ReadinessStep.ChiefDefinitionReady, "agents.chief", state,
            relatedIds: chief.AgentId is null ? [] : [chief.AgentId]);
    }

    private static ReadinessStepContract AgentPool(ReadinessInputs inputs)
    {
        var chief = inputs.Chief;
        ConfigurationState state;
        ReadinessBlocker? blocker = null;
        ReadinessNextAction? nextAction = null;
        if (!chief.AgentPresent)
        {
            state = ConfigurationState.Unconfigured;
            blocker = new ReadinessBlocker("agent.unavailable", []);
            nextAction = new ReadinessNextAction("chief.configureModel", "/agents", null);
        }
        else if (!chief.Healthy)
        {
            state = ConfigurationState.Degraded;
            blocker = new ReadinessBlocker("agent.degraded", chief.AgentId is null ? [] : [chief.AgentId]);
            nextAction = new ReadinessNextAction("agent.recover", "/agents", chief.AgentId);
        }
        else
        {
            state = chief.Simulated ? ConfigurationState.Simulated : ConfigurationState.Ready;
        }

        return Step(ReadinessStep.AgentPoolReady, "agents.pool", state,
            relatedIds: chief.AgentId is null ? [] : [chief.AgentId], blocker: blocker, nextAction: nextAction);
    }

    private static ReadinessStepContract Execution(ReadinessInputs inputs)
    {
        // ExecutionReady só é Ready quando provider+model+workflow+chief resolvem para binding
        // real e verificável. Qualquer dependência apenas simulada rebaixa para Simulated;
        // qualquer ausência bloqueia com as ações agregadas das dependências faltantes.
        var accountReal = inputs.ProviderAccount is { Present: true, Simulated: false };
        var modelReal = inputs.Model is { Present: true, Simulated: false };
        var chiefReal = inputs.Chief is { AgentPresent: true, Healthy: true, ModelResolves: true, Simulated: false };
        var anySimulated =
            (inputs.ProviderAccount.Present && inputs.ProviderAccount.Simulated) ||
            (inputs.Model.Present && inputs.Model.Simulated) ||
            (inputs.Chief.AgentPresent && inputs.Chief.Simulated);

        if (accountReal && modelReal && inputs.WorkflowBound && chiefReal)
        {
            return Step(ReadinessStep.ExecutionReady, "execution", ConfigurationState.Ready,
                nextAction: new ReadinessNextAction("conversation.start", "/projects", inputs.ProjectId));
        }

        var blockers = new List<ReadinessBlocker>();
        if (!inputs.ProviderAccount.Present) blockers.Add(new ReadinessBlocker("provider_account.missing", []));
        if (!inputs.Model.Present) blockers.Add(new ReadinessBlocker("model.none_chat_enabled", []));
        if (!inputs.WorkflowBound) blockers.Add(new ReadinessBlocker("workflow.unbound", []));
        if (!inputs.Chief.AgentPresent || !inputs.Chief.ModelResolves)
            blockers.Add(new ReadinessBlocker("chief.model_unresolved", []));
        // Um Chief presente e resolvível porém não saudável ainda impede execução. Sem este
        // bloqueador a etapa cairia em "não pronta, nenhum bloqueador, inicie uma conversa" —
        // exatamente o tipo de estado contraditório que esta missão elimina.
        var chiefDegraded = inputs.Chief is { AgentPresent: true, ModelResolves: true, Healthy: false };
        if (chiefDegraded)
        {
            blockers.Add(new ReadinessBlocker(
                "agent.degraded", inputs.Chief.AgentId is null ? [] : [inputs.Chief.AgentId]));
        }

        var state = blockers.Count switch
        {
            0 when anySimulated => ConfigurationState.Simulated,
            0 => ConfigurationState.Unconfigured,
            _ when chiefDegraded && blockers.Count == 1 => ConfigurationState.Degraded,
            _ => ConfigurationState.Unconfigured,
        };
        var nextAction = blockers.Count == 0
            ? new ReadinessNextAction("conversation.start", "/projects", inputs.ProjectId)
            : blockers[0].Code switch
            {
                "provider_account.missing" => new ReadinessNextAction("provider.connectAccount", "/providers", null),
                "model.none_chat_enabled" => new ReadinessNextAction("model.enable", "/providers", null),
                "workflow.unbound" => new ReadinessNextAction("workflow.bind", "/workflows", null),
                "agent.degraded" => new ReadinessNextAction("agent.recover", "/agents", inputs.Chief.AgentId),
                _ => new ReadinessNextAction("chief.configureModel", "/agents", null),
            };
        return Step(ReadinessStep.ExecutionReady, "execution", state, blockers: blockers, nextAction: nextAction);
    }

    private static ReadinessStepContract Step(
        ReadinessStep step,
        string capability,
        ConfigurationState state,
        IReadOnlyList<string>? relatedIds = null,
        ReadinessBlocker? blocker = null,
        IReadOnlyList<ReadinessBlocker>? blockers = null,
        ReadinessNextAction? nextAction = null)
    {
        var resolvedBlockers = blockers ?? (blocker is null ? [] : new[] { blocker });
        return new ReadinessStepContract(
            step,
            state,
            ExecutionModeOf(state),
            capability,
            $"readiness.{TokenOf(step)}.{StateToken(state)}",
            relatedIds ?? [],
            resolvedBlockers,
            nextAction);
    }

    private static string ExecutionModeOf(ConfigurationState state) => state switch
    {
        ConfigurationState.Simulated => "simulated",
        ConfigurationState.Unconfigured => "unconfigured",
        _ => "real",
    };

    // Elo "mais fraco" na ordem canônica de prontidão.
    private static ConfigurationState Weakest(ConfigurationState left, ConfigurationState right) =>
        Rank(left) <= Rank(right) ? left : right;

    private static int Rank(ConfigurationState state) => state switch
    {
        ConfigurationState.Unconfigured => 0,
        ConfigurationState.Unavailable => 1,
        ConfigurationState.Degraded => 2,
        ConfigurationState.Simulated => 3,
        ConfigurationState.Configured => 4,
        ConfigurationState.Ready => 5,
        _ => 0,
    };

    private static string StateToken(ConfigurationState state) =>
        char.ToLowerInvariant(state.ToString()[0]) + state.ToString()[1..];

    private static string TokenOf(ReadinessStep step) => step switch
    {
        ReadinessStep.ProfileReady => "profile",
        ReadinessStep.OrganizationReady => "organization",
        ReadinessStep.ProjectReady => "project",
        ReadinessStep.ProviderAccountReady => "providerAccount",
        ReadinessStep.ModelReady => "model",
        ReadinessStep.WorkflowReady => "workflow",
        ReadinessStep.ChiefDefinitionReady => "chiefDefinition",
        ReadinessStep.AgentPoolReady => "agentPool",
        ReadinessStep.ExecutionReady => "execution",
        _ => "unknown",
    };
}
