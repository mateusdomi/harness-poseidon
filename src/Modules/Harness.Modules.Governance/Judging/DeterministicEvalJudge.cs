namespace Harness.Modules.Governance.Judging;

/// <summary>
/// PLAT-04: juiz determinístico baseado em regras — o caminho DEFAULT, sempre disponível e sem
/// credenciais. Aprova quando há critérios de aceite, evidência, um diff e todos os testes passam.
/// O score é a fração de checagens satisfeitas (determinístico, reprodutível em testes).
/// </summary>
public sealed class DeterministicEvalJudge : IEvalJudge
{
    public const string ProviderName = "deterministic-rule-based";

    public string Provider => ProviderName;

    public Task<EvalJudgeVerdict> JudgeAsync(
        EvalJudgeRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(Judge(request));

    public static EvalJudgeVerdict Judge(EvalJudgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.AcceptanceCriteria);
        ArgumentNullException.ThrowIfNull(request.Evidence);
        ArgumentNullException.ThrowIfNull(request.TestResults);

        var reasons = new List<string>();
        var checks = 0;
        var satisfied = 0;

        checks++;
        if (request.AcceptanceCriteria.Count > 0)
        {
            satisfied++;
        }
        else
        {
            reasons.Add("no acceptance criteria supplied");
        }

        checks++;
        if (!string.IsNullOrWhiteSpace(request.Diff))
        {
            satisfied++;
        }
        else
        {
            reasons.Add("no diff supplied");
        }

        checks++;
        if (request.Evidence.Count > 0)
        {
            satisfied++;
        }
        else
        {
            reasons.Add("no execution evidence supplied");
        }

        checks++;
        var failedTests = request.TestResults.Where(test => !test.Passed).ToArray();
        if (request.TestResults.Count > 0 && failedTests.Length == 0)
        {
            satisfied++;
        }
        else if (request.TestResults.Count == 0)
        {
            reasons.Add("no test results supplied");
        }
        else
        {
            reasons.Add($"failing tests: {string.Join(", ", failedTests.Select(test => test.Name))}");
        }

        var passed = satisfied == checks;
        var score = Math.Round((decimal)satisfied / checks, 4);
        var reason = passed
            ? "All acceptance checks satisfied."
            : string.Join("; ", reasons);
        return new EvalJudgeVerdict(passed, reason, score, ProviderName);
    }
}
