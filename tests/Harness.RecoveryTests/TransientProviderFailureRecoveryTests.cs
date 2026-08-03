using Harness.Host.Agents;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Coordination.Application;

namespace Harness.RecoveryTests;

/// <summary>
/// §31 — falha TRANSITÓRIA de provedor.
///
/// É o eixo de recuperação mais fácil de errar em silêncio, porque o sintoma da falha é o
/// mesmo do card ruim: a tentativa termina sem entregar nada. A diferença é de autoria. Se o
/// sistema atribuir ao card o que foi do provedor, o card acumula falhas que não cometeu,
/// queima o circuito, sai do despacho e só volta quando a Bruna o replaneja — replanejamento
/// que não conserta nada, porque o enunciado nunca esteve errado. Foi assim que quatro
/// circuitos abriram por culpa alheia nesta operação.
///
/// A pergunta certa não é "que erro foi esse?", e sim "esta tentativa chegou a JULGAR o
/// enunciado do card?". Um erro de provedor que morre antes de qualquer julgamento não conta.
/// </summary>
public sealed class TransientProviderFailureRecoveryTests
{
    /// <summary>Instabilidade de rede/provedor: repete, e a conta volta sem intervenção humana.</summary>
    [Theory]
    [InlineData("executor.exit_code_1")]
    [InlineData("executor.timeout_connection_reset")]
    [InlineData("executor.start_failed")]
    public void ATransientProviderFailureIsRetriedInsteadOfBlamingTheAccount(string failureCode)
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed, failureCode);

        Assert.Equal(AgentRunOutcomeKind.Transient, outcome.Kind);
        Assert.Null(outcome.SuggestedCooldown);
    }

    /// <summary>
    /// Cota é do PROVEDOR e tem hora para voltar: adia com cooldown em vez de repetir contra a
    /// parede. Repetir aqui era o comportamento que queimava duzentos segundos por rodada e
    /// derrubava um card saudável em três.
    /// </summary>
    [Fact]
    public void QuotaDefersWithACooldownInsteadOfBurningRoundsAgainstTheWall()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            "executor.exit_code_1",
            "rate_limit_error: Usage limit reached");

        Assert.Equal(AgentRunOutcomeKind.QuotaExhausted, outcome.Kind);
        Assert.NotNull(outcome.SuggestedCooldown);
    }

    /// <summary>
    /// A prova que fecha o eixo: os motivos que a infraestrutura produz precisam ser
    /// RECONHECIDOS como dela. É este reconhecimento que impede o serviço de registrar a falha
    /// contra o card — e cada motivo desta lista já custou um circuito aberto por culpa alheia
    /// nesta operação, um de cada vez, conforme apareciam.
    /// </summary>
    [Theory]
    [InlineData("executor.cancelled")]
    [InlineData("run.host_shutdown")]
    [InlineData("executor.quota_exhausted")]
    [InlineData("executor.account_model_unsupported")]
    [InlineData("run.authentication_required")]
    [InlineData("TaskCanceledException")]
    public void AProviderOrHostFailureIsRecognizedAsInfrastructureAndNeverAsTheCardsFault(
        string reason)
    {
        Assert.True(CardCircuitBreakerService.IsInfrastructureFailure(reason));
    }

    /// <summary>
    /// E o contrapeso, para a regra não virar desculpa universal: uma falha que ACONTECEU
    /// depois de o agente produzir trabalho é do card, e continua contando.
    /// </summary>
    [Fact]
    public void AFailureAfterRealWorkStillBelongsToTheCard()
    {
        Assert.False(CardCircuitBreakerService.IsInfrastructureFailure("review.rejected"));

        var circuit = CardCircuitSnapshot.Closed("card-1");
        for (var index = 0; index < CardCircuitBreakerPolicy.ConsecutiveFailureThreshold; index++)
        {
            circuit = CardCircuitBreakerPolicy.RecordFailure(
                circuit, DateTimeOffset.UnixEpoch.AddMinutes(index), "review.rejected");
        }

        Assert.Equal(CardCircuitState.Open, circuit.State);
    }
}
