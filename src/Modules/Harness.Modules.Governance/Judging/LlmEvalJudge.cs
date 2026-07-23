using System.Text;
using System.Text.Json;

namespace Harness.Modules.Governance.Judging;

/// <summary>
/// PLAT-04: costura do juiz real ligado a um LLM. É NEUTRA quanto ao provedor: recebe um transport
/// (a chamada de completude do modelo) por injeção — espelhando como o módulo de agentes separa o
/// executor real do fake. Nunca é o caminho default; só é construída quando
/// <see cref="EvalJudgeFactory"/> confirma que o transport com credencial está configurado
/// (gated como os smokes de executor real). Sem transport → recusa explícita, nunca silenciosa.
/// </summary>
public sealed class LlmEvalJudge(
    string provider,
    Func<string, CancellationToken, Task<string>> completionTransport) : IEvalJudge
{
    private readonly string _provider = string.IsNullOrWhiteSpace(provider)
        ? throw new ArgumentException("Provider is required.", nameof(provider))
        : provider;

    private readonly Func<string, CancellationToken, Task<string>> _completionTransport =
        completionTransport ?? throw new ArgumentNullException(nameof(completionTransport));

    public string Provider => _provider;

    public async Task<EvalJudgeVerdict> JudgeAsync(
        EvalJudgeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prompt = BuildPrompt(request);
        var raw = await _completionTransport(prompt, cancellationToken).ConfigureAwait(false);
        return Parse(raw, _provider);
    }

    // Prompt determinístico a partir do critério + evidência. O transport decide como falar com o
    // modelo; aqui apenas montamos a mensagem e interpretamos a resposta JSON.
    public static string BuildPrompt(EvalJudgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var builder = new StringBuilder();
        builder.AppendLine(
            "You are an independent evaluator. Judge whether the attempt satisfies the acceptance");
        builder.AppendLine(
            "criteria given the evidence. Reply with JSON {\"passed\":bool,\"score\":0..1,\"reason\":string}.");
        builder.AppendLine("## Acceptance criteria");
        foreach (var criterion in request.AcceptanceCriteria)
        {
            builder.Append("- ").AppendLine(criterion);
        }

        builder.AppendLine("## Diff");
        builder.AppendLine(request.Diff);
        builder.AppendLine("## Evidence");
        foreach (var evidence in request.Evidence)
        {
            builder.Append("- ").AppendLine(evidence);
        }

        builder.AppendLine("## Test results");
        foreach (var test in request.TestResults)
        {
            builder.Append("- ").Append(test.Name).Append(": ")
                .AppendLine(test.Passed ? "passed" : "failed");
        }

        return builder.ToString();
    }

    public static EvalJudgeVerdict Parse(string raw, string provider)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new EvalJudgeUnavailableException("judge.empty_response");
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var passed = root.TryGetProperty("passed", out var passedElement) &&
                passedElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                passedElement.GetBoolean();
            var score = root.TryGetProperty("score", out var scoreElement) &&
                scoreElement.ValueKind == JsonValueKind.Number
                ? Math.Clamp(scoreElement.GetDecimal(), 0m, 1m)
                : passed ? 1m : 0m;
            var reason = root.TryGetProperty("reason", out var reasonElement) &&
                reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString() ?? string.Empty
                : string.Empty;
            return new EvalJudgeVerdict(passed, reason, score, provider);
        }
        catch (JsonException exception)
        {
            throw new EvalJudgeUnavailableException($"judge.invalid_response:{exception.Message}");
        }
    }
}
