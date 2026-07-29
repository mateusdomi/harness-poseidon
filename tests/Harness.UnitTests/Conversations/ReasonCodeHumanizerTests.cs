using Harness.Modules.Conversations.Application;

namespace Harness.UnitTests.Conversations;

/// <summary>
/// F2/D15 — o exemplo homologado tinha a Bruna dizendo <c>account.role_not_allowed</c> ao dono.
/// Um código cru não é só feio: é uma não-resposta, e custa dois turnos de conversa para não
/// dizer nada.
/// </summary>
public sealed class ReasonCodeHumanizerTests
{
    /// <summary>Vocabulário que jamais pode aparecer numa frase mostrada ao dono.</summary>
    private static readonly string[] ForbiddenVocabulary =
    [
        "worktree", "lease", "fencing", "provider", "token", "tenant", "slug",
        "heartbeat", "gate", "card", "risk", "quota", "runner", "attempt",
        "circuit", "payload", "endpoint", "commit", "branch"
    ];

    /// <summary>Gate da fase: nenhum código cru chega ao dono — nem os que ninguém traduziu.</summary>
    [Theory]
    [InlineData("account.role_not_allowed")]
    [InlineData("card.circuit_open")]
    [InlineData("chief_loop.causal_cycle")]
    [InlineData("authority.stale_fencing")]
    [InlineData("codigo.que.ninguem.mapeou")]
    [InlineData("account.some_brand_new_code")]
    [InlineData("")]
    [InlineData(null)]
    public void NoRawCodeEverReachesTheOwner(string? code)
    {
        var text = ReasonCodeHumanizer.Humanize(code).Compose();

        Assert.False(string.IsNullOrWhiteSpace(text));
        if (!string.IsNullOrWhiteSpace(code))
        {
            Assert.DoesNotContain(code, text, StringComparison.OrdinalIgnoreCase);
        }

        // Um código tem a forma "familia.motivo_tecnico"; a frase humana não tem essa forma.
        Assert.DoesNotContain("_", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownCodeAdmitsIgnoranceInsteadOfInventingAnExplanation()
    {
        var reason = ReasonCodeHumanizer.Humanize("account.codigo_inedito");

        Assert.Equal(ReasonCodeHumanizer.Unknown, reason);
        Assert.False(ReasonCodeHumanizer.HasExplicitTranslation("account.codigo_inedito"));
        Assert.Contains("verificando", reason.Phrase, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gate da fase: toda frase traduzida está em linguagem de negócio.</summary>
    [Fact]
    public void EveryTranslationIsFreeOfTechnicalVocabulary()
    {
        foreach (var code in ReasonCodeHumanizer.KnownCodes)
        {
            var text = ReasonCodeHumanizer.Humanize(code).Compose();
            foreach (var forbidden in ForbiddenVocabulary)
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void TheHomologatedExampleBecomesAHumanSentenceWithANextStep()
    {
        var reason = ReasonCodeHumanizer.Humanize("account.role_not_allowed");

        Assert.Contains("competência", reason.Phrase, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(reason.NextStep);
        Assert.Contains("?", reason.NextStep!, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTranslationOffersANextStepUnlessThereIsNothingToDo()
    {
        var withoutNextStep = ReasonCodeHumanizer.KnownCodes
            .Where(code => ReasonCodeHumanizer.Humanize(code).NextStep is null)
            .ToArray();

        // Só o caso em que não há nada a fazer pode ficar sem próximo passo; inventar uma ação
        // onde não existe uma é pior do que calar.
        Assert.Equal(["completion.already_completed"], withoutNextStep);
    }

    [Fact]
    public void ComposeJoinsWhatHappenedWithWhatToDo()
    {
        var reason = new HumanReason("Aconteceu isto.", "Faça aquilo.");
        Assert.Equal("Aconteceu isto. Faça aquilo.", reason.Compose());

        Assert.Equal("Somente isto.", new HumanReason("Somente isto.").Compose());
    }

    [Fact]
    public void EscalationIsPresentedAsAQuestionNotAsAFailure()
    {
        var text = ReasonCodeHumanizer.Humanize("wait.escalated").Compose();

        Assert.Contains("dúvida", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("falha", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("erro", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ALateDeadlineBlamesTheEstimateNotThePerson()
    {
        var text = ReasonCodeHumanizer.Humanize("wait.deadline_checkpoint").Compose();

        Assert.Contains("estimativa", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("culpa", text, StringComparison.OrdinalIgnoreCase);
    }
}
