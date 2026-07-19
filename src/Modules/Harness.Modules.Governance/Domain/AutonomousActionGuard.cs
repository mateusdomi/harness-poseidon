namespace Harness.Modules.Governance.Domain;

public enum GuardedActionKind
{
    ProductionDeploy,
    DataDeletion,
    PolicyChange,
    ProtectedBranchPush,
    BudgetOverrun,
    UnapprovedBusinessRuleChange,
    UnauthorizedCredentialUse,
}

public enum GuardedActorKind
{
    Human,
    Chief,
    Agent,
    System,
}

public sealed record GuardedActionRequest(
    GuardedActionKind Action,
    GuardedActorKind Actor,
    string OperationMode,
    string? HumanApprovalId = null);

public sealed record GuardedActionDecision(bool Allowed, string Code, string Detail)
{
    public static GuardedActionDecision Permit(string code) =>
        new(true, code, "The guarded action carries an explicit human approval.");

    public static GuardedActionDecision Deny(string code, string detail) =>
        new(false, code, detail);
}

/// <summary>
/// Bloqueios invioláveis da missão: nenhuma automação — em nenhum modo de operação,
/// inclusive autônomo — pode executar as ações guardadas. Somente um humano com
/// aprovação explícita registrada atravessa o guard.
/// </summary>
public static class AutonomousActionGuard
{
    private static readonly HashSet<string> OperationModes =
        new(["manual", "semiautonomous", "autonomous"], StringComparer.Ordinal);

    public static GuardedActionDecision Evaluate(GuardedActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperationModes.Contains(request.OperationMode))
        {
            throw new ArgumentException(
                "The operation mode is not part of the closed set.",
                nameof(request));
        }

        var code = CodeFor(request.Action);
        if (request.Actor != GuardedActorKind.Human)
        {
            return GuardedActionDecision.Deny(
                code,
                $"A ação guardada {code} é inviolável para atores automatizados em qualquer " +
                $"modo de operação, inclusive '{request.OperationMode}'.");
        }

        return string.IsNullOrWhiteSpace(request.HumanApprovalId)
            ? GuardedActionDecision.Deny(
                code,
                $"A ação guardada {code} exige aprovação humana explícita registrada.")
            : GuardedActionDecision.Permit(code);
    }

    public static string CodeFor(GuardedActionKind action) => action switch
    {
        GuardedActionKind.ProductionDeploy => "production_deploy",
        GuardedActionKind.DataDeletion => "data_deletion",
        GuardedActionKind.PolicyChange => "policy_change",
        GuardedActionKind.ProtectedBranchPush => "protected_branch_push",
        GuardedActionKind.BudgetOverrun => "budget_overrun",
        GuardedActionKind.UnapprovedBusinessRuleChange => "unapproved_business_rule_change",
        GuardedActionKind.UnauthorizedCredentialUse => "unauthorized_credential_use",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown guarded action."),
    };
}
