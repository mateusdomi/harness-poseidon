namespace Harness.Host.WorkBoard;

public enum ProductE2EGateDecision
{
    Passed,
    Failed,
    Unavailable,
}

public static class ProductE2EGatePolicy
{
    public static ProductE2EGateDecision Decide(ProductE2EResult result) =>
        result switch
        {
            { Ran: true, Passed: true } => ProductE2EGateDecision.Passed,
            { Ran: true, Passed: false } => ProductE2EGateDecision.Failed,
            { Ran: false } when result.Detail.Contains(
                "referencia variável sem provedor runtime",
                StringComparison.OrdinalIgnoreCase) => ProductE2EGateDecision.Failed,
            { Ran: false } when result.Detail.Contains(
                "worktree Git não está limpa",
                StringComparison.OrdinalIgnoreCase) => ProductE2EGateDecision.Failed,
            _ => ProductE2EGateDecision.Unavailable,
        };
}
