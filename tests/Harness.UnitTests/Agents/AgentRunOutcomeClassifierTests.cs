using System.Globalization;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Application.Execution.External;

namespace Harness.UnitTests.Agents;

/// <summary>
/// A base do retry inteligente: o desfecho de uma execução é classificado em ADIAR (cota),
/// ESCALAR (login/permanente) ou REPETIR (transitório). O GLM/Z.AI é instável e cai com
/// falhas transitórias, que devem ser candidatas a retry — não a escalonamento humano.
/// </summary>
public sealed class AgentRunOutcomeClassifierTests
{
    [Fact]
    public void ACompletedRunNeitherRetriesNorEscalates()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(ExternalAgentRunStatus.Completed, null);
        Assert.Equal(AgentRunOutcomeKind.Completed, outcome.Kind);
        Assert.False(outcome.ShouldRetry);
        Assert.False(outcome.NeedsHuman);
    }

    [Theory]
    [InlineData("executor.quota_exhausted")]
    [InlineData("quota.exhausted")]
    [InlineData("provider.rate_limit")]
    [InlineData("http_429_too_many_requests")]
    [InlineData("resource_exhausted")]
    public void AQuotaSignalWaitsForResetWithAConservativeCooldown(string failureCode)
    {
        var outcome = AgentRunOutcomeClassifier.Classify(ExternalAgentRunStatus.Failed, failureCode);
        Assert.Equal(AgentRunOutcomeKind.QuotaExhausted, outcome.Kind);
        Assert.True(outcome.ShouldWaitForReset);
        Assert.False(outcome.ShouldRetry);
        Assert.Equal(AgentRunOutcomeClassifier.DefaultQuotaCooldown, outcome.SuggestedCooldown);
    }

    [Theory]
    [InlineData("executor.authentication_required")]
    [InlineData("Not logged in")]
    [InlineData("http_401_unauthorized")]
    public void AnAuthenticationSignalNeedsAHumanAndNeverRetriesAlone(string failureCode)
    {
        var outcome = AgentRunOutcomeClassifier.Classify(ExternalAgentRunStatus.Failed, failureCode);
        Assert.Equal(AgentRunOutcomeKind.AuthenticationRequired, outcome.Kind);
        Assert.True(outcome.NeedsHuman);
        Assert.False(outcome.ShouldRetry);
    }

    [Theory]
    [InlineData(ExternalAgentRunStatus.TimedOut, null)]
    [InlineData(ExternalAgentRunStatus.Failed, "executor.exit_code_1")]
    [InlineData(ExternalAgentRunStatus.Failed, "connection reset by peer")]
    [InlineData(ExternalAgentRunStatus.Failed, "executor.no_output")]
    [InlineData(ExternalAgentRunStatus.Failed, "upstream 503 overloaded")]
    public void ATransientFailureIsACandidateForRetry(ExternalAgentRunStatus status, string? failureCode)
    {
        // O GLM instável cai justamente aqui — retry com backoff, não escalonamento.
        var outcome = AgentRunOutcomeClassifier.Classify(status, failureCode);
        Assert.Equal(AgentRunOutcomeKind.Transient, outcome.Kind);
        Assert.True(outcome.ShouldRetry);
        Assert.False(outcome.NeedsHuman);
    }

    [Fact]
    public void AnUnrecognizedFailureIsPermanentAndEscalatesInsteadOfRetryingBlindly()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(ExternalAgentRunStatus.Failed, "critic.pass_contradicted_by_findings");
        Assert.Equal(AgentRunOutcomeKind.Permanent, outcome.Kind);
        Assert.True(outcome.NeedsHuman);
        Assert.False(outcome.ShouldRetry);
    }

    [Fact]
    public void ACancelledRunIsNeverRetried()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(ExternalAgentRunStatus.Cancelled, "executor.cancelled");
        Assert.Equal(AgentRunOutcomeKind.Cancelled, outcome.Kind);
        Assert.False(outcome.ShouldRetry);
    }

    /// <summary>
    /// Cota esgotada que a CLI não traduz em código estruturado.
    ///
    /// Observado no E2E de empréstimos: o GLM/Z.AI respondeu
    /// `429 rate_limit_error [1308] Usage limit reached for 5 hour`, a CLI imprimiu isso no erro
    /// padrão e saiu com código 1. Só `executor.exit_code_1` chegava aqui — e ele casa com o sinal
    /// `exit_code` da lista TRANSITÓRIA. A conta voltava de um cooldown curto, era reeleita,
    /// queimava mais ~200s sem produzir um token e derrubava outra rodada do card. Três dessas e
    /// um card saudável morria por um problema que era da conta.
    /// </summary>
    [Fact]
    public void QuotaExhaustionIsRecognizedFromTheExecutorDiagnosticWhenTheCodeOnlySaysExitCode()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            "executor.exit_code_1",
            "{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"code\":\"1308\"," +
            "\"message\":\"[1308][Usage limit reached for 5 hour. Your limit will reset at " +
            "2026-08-03 06:00:40]\"}}");

        Assert.Equal(AgentRunOutcomeKind.QuotaExhausted, outcome.Kind);
        Assert.True(outcome.ShouldWaitForReset);
        Assert.NotNull(outcome.SuggestedCooldown);
    }

    /// <summary>
    /// Sem sinal de cota no diagnóstico, um exit code continua sendo instabilidade — o diagnóstico
    /// amplia o reconhecimento de cota, não reclassifica tudo.
    /// </summary>
    [Fact]
    public void AnOrdinaryExitCodeStaysTransientEvenWithADiagnostic()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            "executor.exit_code_1",
            "TypeError: cannot read property 'map' of undefined");

        Assert.Equal(AgentRunOutcomeKind.Transient, outcome.Kind);
    }

    /// <summary>
    /// A frase EXATA da CLI é reconhecida no diagnóstico: observado ao vivo na prova limpa — a
    /// conta claude-secondary, cuja credencial vive no Keychain do macOS (inalcançável de dentro
    /// do contêiner), falhou com `exit_code_1` e "Not logged in · Please run /login" no erro
    /// padrão. Como transitória, ela voltava em minutos e queimava o circuito de cards
    /// saudáveis; como autenticação, ela sai da eleição até um humano decidir.
    /// </summary>
    [Fact]
    public void AuthenticationIsRecognizedFromTheExactCliPhraseInTheDiagnostic()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            "executor.exit_code_1",
            "Not logged in · Please run /login");

        Assert.Equal(AgentRunOutcomeKind.AuthenticationRequired, outcome.Kind);
        Assert.True(outcome.NeedsHuman);
        Assert.False(outcome.ShouldRetry);
    }

    /// <summary>
    /// A conta que não consegue servir modelo nenhum do CLI instalado sai da eleição como
    /// "precisa de humano", com código próprio — classificar como permanente escalaria o CARD
    /// por culpa da conta (observado ao vivo com o backend do ChatGPT e o codex 0.44.0).
    /// </summary>
    [Fact]
    public void AnUnsupportedAccountModelNeedsAHumanAndDoesNotEscalateTheCard()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            "executor.exit_code_1",
            "unexpected status 400 Bad Request: {\"detail\":\"The 'gpt-5-codex' model is not " +
            "supported when using Codex with a ChatGPT account.\"}");

        Assert.Equal(AgentRunOutcomeKind.AuthenticationRequired, outcome.Kind);
        Assert.Equal("run.account_model_unsupported", outcome.ReasonCode);
        Assert.True(outcome.NeedsHuman);
        Assert.False(outcome.ShouldRetry);
    }

    /// <summary>
    /// Cota SEMANAL não volta em três horas. Quando o provedor declara o instante do reset, o
    /// cooldown é ELE — senão a conta reaparece elegível a cada três horas, é eleita, e o card
    /// morre de novo. Foi o laço que consumiu a madrugada de 2026-08-03 com o GLM.
    /// </summary>
    [Fact]
    public void AQuotaCooldownHonoursTheResetInstantTheProviderDeclared()
    {
        var now = DateTimeOffset.Parse("2026-08-03T04:56:00Z", CultureInfo.InvariantCulture);
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            "executor.no_progress",
            "[ERROR] Error streaming, falling back to non-streaming mode: 429 " +
            "{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"code\":\"1310\"," +
            "\"message\":\"[1310][Weekly/Monthly Limit Exhausted. Your limit will reset at " +
            "2026-08-06 10:11:22]\"}}",
            now);

        Assert.Equal(AgentRunOutcomeKind.QuotaExhausted, outcome.Kind);
        Assert.NotNull(outcome.SuggestedCooldown);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-06T10:11:22Z", CultureInfo.InvariantCulture) - now,
            outcome.SuggestedCooldown!.Value);
        Assert.True(outcome.SuggestedCooldown.Value > AgentRunOutcomeClassifier.DefaultQuotaCooldown);
    }

    /// <summary>Sem instante declarado, o padrão conservador continua valendo.</summary>
    [Fact]
    public void AQuotaWithoutADeclaredResetKeepsTheConservativeDefault()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed, "executor.exit_code_1", "429 rate_limit_error");

        Assert.Equal(AgentRunOutcomeKind.QuotaExhausted, outcome.Kind);
        Assert.Equal(AgentRunOutcomeClassifier.DefaultQuotaCooldown, outcome.SuggestedCooldown);
    }

    [Fact]
    public void ProviderResourcePackageExhaustionIsQuotaEvenWithoutClassicQuotaWords()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            "executor.exit_code_1",
            "API Error: Request rejected · [1113][Insufficient balance or no resource package. Please recharge.]");

        Assert.Equal(AgentRunOutcomeKind.QuotaExhausted, outcome.Kind);
        Assert.True(outcome.ShouldWaitForReset);
        Assert.Equal(AgentRunOutcomeClassifier.DefaultQuotaCooldown, outcome.SuggestedCooldown);
    }

    /// <summary>
    /// Um reset já vencido não vale nada — a cota deveria ter voltado. Confiar no texto
    /// aposentaria a conta por engano; o padrão curto testa de novo.
    /// </summary>
    [Fact]
    public void AResetInThePastFallsBackToTheDefaultCooldown()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            "executor.exit_code_1",
            "rate_limit_error: your limit will reset at 2020-01-01 00:00:00",
            DateTimeOffset.Parse("2026-08-03T04:56:00Z", CultureInfo.InvariantCulture));

        Assert.Equal(AgentRunOutcomeClassifier.DefaultQuotaCooldown, outcome.SuggestedCooldown);
    }

    /// <summary>Texto corrompido não pode aposentar uma conta para sempre.</summary>
    [Fact]
    public void ADeclaredResetIsCappedSoATypoCannotRetireTheAccount()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            "executor.exit_code_1",
            "rate_limit_error: limit will reset at 2099-01-01 00:00:00",
            DateTimeOffset.Parse("2026-08-03T04:56:00Z", CultureInfo.InvariantCulture));

        Assert.Equal(AgentRunOutcomeClassifier.MaximumQuotaCooldown, outcome.SuggestedCooldown);
    }

    /// <summary>
    /// Travamento por silêncio é da CONTA quando o diagnóstico diz cota: sem isso ele casaria
    /// o sinal transitório e faria tudo de novo na mesma conta morta.
    /// </summary>
    [Fact]
    public void ASilentRunWithAQuotaDiagnosticIsQuotaAndNotTransient()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            "executor.no_progress",
            "429 rate_limit_error Weekly/Monthly Limit Exhausted");

        Assert.Equal(AgentRunOutcomeKind.QuotaExhausted, outcome.Kind);
        Assert.False(outcome.ShouldRetry);
        Assert.True(outcome.ShouldWaitForReset);
    }

    /// <summary>
    /// Sem diagnóstico, silêncio é instabilidade: repete. Classificar como permanente mataria
    /// o card por uma CLI que emudeceu.
    /// </summary>
    [Fact]
    public void ASilentRunWithoutADiagnosticIsTransient()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed, "executor.no_progress");

        Assert.Equal(AgentRunOutcomeKind.Transient, outcome.Kind);
        Assert.True(outcome.ShouldRetry);
        Assert.False(outcome.NeedsHuman);
    }

    /// <summary>
    /// Autenticação NÃO é inferida de texto solto: ela exige ação humana e não se recupera
    /// sozinha, então um falso positivo vindo do erro padrão pararia a conta até alguém
    /// intervir. Só as frases exatas da CLI qualificam — "unauthorized" de trabalho, não.
    /// </summary>
    [Fact]
    public void AuthenticationIsNeverInferredFromTheDiagnostic()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            "executor.exit_code_1",
            "warning: unauthorized access to /tmp/cache ignored");

        Assert.NotEqual(AgentRunOutcomeKind.AuthenticationRequired, outcome.Kind);
    }
}
