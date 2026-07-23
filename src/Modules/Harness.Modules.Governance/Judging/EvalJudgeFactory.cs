namespace Harness.Modules.Governance.Judging;

/// <summary>
/// PLAT-04: resolve o <see cref="IEvalJudge"/> ativo. Default seguro: determinístico e sempre
/// disponível. O juiz real ligado a um LLM só é selecionado quando explicitamente habilitado
/// (<see cref="EvalJudgeOptions.UseLlmJudge"/>) E há um transport com credencial — caso contrário,
/// recai no determinístico. Espelha <c>ExternalAgentExecutorFactory</c>: um seletor explícito, sem
/// substituição silenciosa por um caminho não configurado.
/// </summary>
public sealed class EvalJudgeFactory(
    EvalJudgeOptions options,
    Func<string, CancellationToken, Task<string>>? llmTransport = null)
{
    private readonly EvalJudgeOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    private readonly Func<string, CancellationToken, Task<string>>? _llmTransport = llmTransport;

    public bool LlmJudgeAvailable => _options.UseLlmJudge && _llmTransport is not null;

    public IEvalJudge Create() => LlmJudgeAvailable
        ? new LlmEvalJudge(_options.Provider, _llmTransport!)
        : new DeterministicEvalJudge();
}

public sealed record EvalJudgeOptions
{
    /// <summary>
    /// Liga a seleção do juiz real. Default DESLIGADO: o determinístico é o caminho padrão e os
    /// testes permanecem sem credenciais. Mesmo ligado, sem transport configurado recai no default.
    /// </summary>
    public bool UseLlmJudge { get; init; }

    public string Provider { get; init; } = "deterministic-rule-based";
}
