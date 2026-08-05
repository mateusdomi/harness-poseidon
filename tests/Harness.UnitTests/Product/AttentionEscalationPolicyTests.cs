using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Product;

/// <summary>
/// Human Attention Loop — a escadinha determinística, provada com relógio FAKE e zero sleeps.
/// A IA decide o significado da dúvida; o CÓDIGO decide como escalá-la — e este arquivo é o
/// contrato desse código: T+0 in-app, T+1m Telegram, T+15m lembrete, T+60m final, depois
/// silêncio. Timeout NUNCA vira resposta.
/// </summary>
public sealed class AttentionEscalationPolicyTests
{
    private static readonly DateTimeOffset Created = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly AttentionEscalationOptions Options = AttentionEscalationOptions.Default;

    private static AttentionDecision Decide(string status, int reminders, TimeSpan age) =>
        AttentionEscalationPolicy.Decide(status, reminders, Created, Created + age, Options);

    /// <summary>O cenário completo da Parte 19, marco a marco.</summary>
    [Fact]
    public void AEscadinhaSegueExatamenteOsMarcosDaPolitica()
    {
        // t=0 — pedido aberto: in-app imediato.
        var t0 = Decide("open", 0, TimeSpan.Zero);
        Assert.Equal(AttentionAction.NotifyInApp, t0.Action);
        Assert.Equal("notified", t0.NextStatus);

        // t=30s — ainda antes do marco do Telegram: nada.
        Assert.Equal(AttentionAction.None, Decide("notified", 0, TimeSpan.FromSeconds(30)).Action);

        // t=1m — Telegram, UMA vez.
        var t1 = Decide("notified", 0, TimeSpan.FromMinutes(1));
        Assert.Equal(AttentionAction.NotifyTelegram, t1.Action);
        Assert.Equal(1, t1.NextReminderCount);

        // t=5m — entre marcos: nada (não há reenvio por tick).
        Assert.Equal(AttentionAction.None, Decide("notified", 1, TimeSpan.FromMinutes(5)).Action);

        // t=15m — sem resposta: lembrete.
        var t15 = Decide("notified", 1, TimeSpan.FromMinutes(15));
        Assert.Equal(AttentionAction.RemindTelegram, t15.Action);

        // t=60m — lembrete FINAL; next_action_at nulo = nunca mais.
        var t60 = Decide("notified", 2, TimeSpan.FromMinutes(60));
        Assert.Equal(AttentionAction.FinalReminder, t60.Action);
        Assert.Null(t60.NextActionAt);

        // t=2h, t=12h, t=3 dias — SILÊNCIO. Spam periódico infinito destrói o canal.
        Assert.Equal(AttentionAction.None, Decide("notified", 3, TimeSpan.FromHours(2)).Action);
        Assert.Equal(AttentionAction.None, Decide("notified", 3, TimeSpan.FromHours(12)).Action);
        Assert.Equal(AttentionAction.None, Decide("notified", 3, TimeSpan.FromDays(3)).Action);
    }

    /// <summary>Reconhecido = a pessoa VIU. Insistir depois disso é ruído, não diligência.</summary>
    [Fact]
    public void ReconhecimentoParaOsLembretesAgressivos()
    {
        Assert.Equal(
            AttentionAction.None,
            Decide("acknowledged", 1, TimeSpan.FromMinutes(30)).Action);
        Assert.Equal(
            AttentionAction.None,
            Decide("acknowledged", 1, TimeSpan.FromHours(5)).Action);
    }

    /// <summary>
    /// NO RESPONSE DOES NOT INFER: a política não possui NENHUMA ação que produza resposta —
    /// esgotados os lembretes, o pedido permanece como está (o subgrafo dependente segue
    /// bloqueado; o trabalho independente segue livre). O relógio não tem autoridade.
    /// </summary>
    [Fact]
    public void TimeoutNuncaViraResposta()
    {
        var exhausted = Decide("notified", 3, TimeSpan.FromDays(7));

        Assert.Equal(AttentionAction.None, exhausted.Action);
        Assert.Equal("notified", exhausted.NextStatus);
        Assert.DoesNotContain(
            Enum.GetNames<AttentionAction>(),
            name => name.Contains("Answer", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Infer", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Assume", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Partes 20/21 — quem cria pedido é SÓ o ASK. A pergunta de volumetria (NON_BLOCKING /
    /// DEFER) não interrompe ninguém; a divergência de regra de negócio (ASK) interrompe.
    /// A distinção vem da classificação semântica, nunca de "toda pergunta vira alarme".
    /// </summary>
    [Theory]
    [InlineData("Quantos usuários simultâneos esperamos em produção?", false)]
    [InlineData("Há duas regras contraditórias sobre quem pode aprovar a reversão de uma carga; qual delas vale?", true)]
    public void SoAskGenuinoInterrompeOHumano(string question, bool interromperia)
    {
        // O contrato: DEFER/INFER registram e seguem; ASK cria HumanAttentionRequest. A
        // pergunta de capacidade tem default seguro (não muda arquitetura agora); a contradição
        // de regra de negócio não tem default — só autoridade humana resolve.
        var classified = SourceKnowledgeClassifier.Classify(question, "intake");

        if (interromperia)
        {
            // Contradição de regra: nada no texto a rebaixa a preferência/futuro/processo.
            Assert.Equal(KnowledgeBinding.Required, classified.Binding);
        }
        else
        {
            Assert.NotEqual(KnowledgeBinding.Required, classified.Binding);
        }
    }
}
