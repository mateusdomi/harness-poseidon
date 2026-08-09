namespace Harness.Host.WorkBoard;

internal enum ProductE2EGateDecision
{
    Passed,
    Failed,
    Unavailable,
}

internal static class ProductE2EGatePolicy
{
    public static ProductE2EGateDecision Decide(ProductE2EResult result) =>
        result switch
        {
            { Ran: true, Passed: true } => ProductE2EGateDecision.Passed,
            { Ran: true, Passed: false } => ProductE2EGateDecision.Failed,
            _ => ProductE2EGateDecision.Unavailable,
        };
}
