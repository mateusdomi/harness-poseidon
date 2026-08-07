using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Application.Execution.External;

namespace Harness.UnitTests.Agents;

/// <summary>
/// INC-EVAL-001 — contas do papel <c>critic</c> rodam a CLI Claude Code atrás de um
/// <c>ANTHROPIC_BASE_URL</c> alternativo que rejeita combinações de <c>--model</c>/<c>--effort</c>
/// que o executor "claude-code" declara suportar em geral (a capacidade é do EXECUTOR,
/// <see cref="Harness.Modules.Agents.Contracts.CapabilitySet.SupportsEffort"/>, não da CONTA
/// específica). A CLI recusa o argumento fora do envelope stream-json, sai com código 1 — igual
/// ao caso de credencial ausente — e SEM esta tradução o classificador genérico via só
/// <c>executor.exit_code_1</c>, que casa o sinal transitório "exit_code" e vira retry cego na
/// MESMA conta com o MESMO argumento inválido, para sempre. Medido ao vivo em 2026-08-06: 5
/// tentativas queimadas, a conta marcada `quota_limited` sem hora de retorno, backlog inteiro em
/// `awaiting_account_return`.
/// </summary>
public sealed class ClaudeCodeInvalidModelSelectionDiagnosticTests
{
    private const string EffortRejected =
        "Error: invalid model selection (--model \"opus\" --effort \"medium\"): " +
        "--effort is not supported for model \"opus\"";

    private const string ModelRejected = "model opus is not recognized as a known model";

    [Fact]
    public void EffortRejectedOnStandardErrorBecomesAccountModelUnsupported()
    {
        var parser = new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser();

        parser.ObserveErrorLine(EffortRejected);

        Assert.Equal("executor.invalid_model_selection", parser.FailureCode);
        Assert.Equal(ExternalFailureKind.AccountModelUnsupported, parser.FailureKind);
    }

    /// <summary>
    /// A segunda iteração do INC-EVAL-001: com o effort removido, a mesma conta passou a
    /// recusar o `--model` também — frase diferente, mesma família de causa.
    /// </summary>
    [Fact]
    public void ModelRejectedOnStandardErrorBecomesAccountModelUnsupported()
    {
        var parser = new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser();

        parser.ObserveErrorLine(ModelRejected);

        Assert.Equal("executor.invalid_model_selection", parser.FailureCode);
        Assert.Equal(ExternalFailureKind.AccountModelUnsupported, parser.FailureKind);
    }

    /// <summary>
    /// A rejeição sai FORA do envelope stream-json (a CLI nunca entra no protocolo) — mesma via
    /// não-estruturada que carrega "Not logged in". <see cref="ParseLine"/> não pode perder o
    /// sinal só porque a linha não é JSON.
    /// </summary>
    [Fact]
    public void RejectionOutsideTheJsonEnvelopeIsStillCaught()
    {
        var parser = new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser();

        Assert.Empty(parser.ParseLine(EffortRejected).ToArray());

        Assert.Equal("executor.invalid_model_selection", parser.FailureCode);
        Assert.Equal(ExternalFailureKind.AccountModelUnsupported, parser.FailureKind);
    }

    /// <summary>
    /// A prova do incidente: ANTES desta correção, o desfecho para este texto (com o código cru
    /// `executor.exit_code_1` que a CLI realmente produz na saída não-estruturada) caía no sinal
    /// transitório "exit_code" — retry cego. Com o adaptador classificando estruturalmente,
    /// `Classify` nunca alcança o caminho de heurística de texto: vai direto para
    /// `run.account_model_unsupported`.
    /// </summary>
    [Fact]
    public void TheClassifiedOutcomeNeedsAHumanInsteadOfRetryingBlindly()
    {
        var desfecho = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            ExternalFailureKind.AccountModelUnsupported,
            "executor.invalid_model_selection",
            EffortRejected);

        Assert.Equal("run.account_model_unsupported", desfecho.ReasonCode);
        Assert.True(desfecho.NeedsHuman);
        Assert.False(desfecho.ShouldRetry);
        Assert.False(desfecho.ShouldWaitForReset);
    }

    /// <summary>
    /// Nunca cota: o erro é de argumento, não de limite. Confundir os dois (o sintoma original
    /// do INC-EVAL-001, contornado só por dado) marcava a conta indisponível sem hora de volta.
    /// </summary>
    [Fact]
    public void TheOutcomeIsNeverClassifiedAsQuota()
    {
        var desfecho = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            ExternalFailureKind.AccountModelUnsupported,
            "executor.invalid_model_selection",
            EffortRejected);

        Assert.NotEqual(AgentRunOutcomeKind.QuotaExhausted, desfecho.Kind);
    }

    /// <summary>
    /// Defesa em profundidade: mesmo sem o adaptador classificar estruturalmente (executor não
    /// migrado, ou um camino que só tenha o texto cru), o classificador de texto reconhece as
    /// mesmas frases e chega ao mesmo desfecho — nunca ao transitório genérico de "exit_code".
    /// </summary>
    [Fact]
    public void TheTextFallbackClassifierAlsoRecognizesTheSamePhrases()
    {
        var desfecho = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed, "executor.exit_code_1", EffortRejected);

        Assert.Equal("run.account_model_unsupported", desfecho.ReasonCode);
        Assert.NotEqual(AgentRunOutcomeKind.QuotaExhausted, desfecho.Kind);
        Assert.NotEqual(AgentRunOutcomeKind.Transient, desfecho.Kind);
    }
}
