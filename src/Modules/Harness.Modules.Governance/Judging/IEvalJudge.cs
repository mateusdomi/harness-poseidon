namespace Harness.Modules.Governance.Judging;

/// <summary>
/// PLAT-04: costura de LLM-as-judge. O juiz PONTUA uma tentativa (pass/fail + razão + score
/// opcional) dado o critério de aceite e a evidência.
///
/// O caminho DEFAULT (<see cref="DeterministicEvalJudge"/>) é determinístico e sempre disponível —
/// sem credenciais. A implementação real ligada a um LLM entra pela mesma interface e é selecionada
/// por <see cref="EvalJudgeFactory"/> (espelhando a separação executor fake × real do módulo de
/// agentes). Assíncrona porque o juiz real faz I/O; o default retorna imediatamente.
/// </summary>
public interface IEvalJudge
{
    string Provider { get; }

    Task<EvalJudgeVerdict> JudgeAsync(
        EvalJudgeRequest request, CancellationToken cancellationToken = default);
}

public sealed record EvalJudgeRequest(
    IReadOnlyList<string> AcceptanceCriteria,
    string Diff,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<EvalJudgeTestResult> TestResults);

public sealed record EvalJudgeTestResult(string Name, bool Passed, string EvidenceReference);

public sealed record EvalJudgeVerdict(
    bool Passed, string Reason, decimal Score, string Provider);

public sealed class EvalJudgeUnavailableException(string reason)
    : Exception(reason)
{
    public string Reason { get; } = reason;
}
