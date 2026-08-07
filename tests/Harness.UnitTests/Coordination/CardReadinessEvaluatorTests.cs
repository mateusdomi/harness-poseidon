using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class CardReadinessEvaluatorTests
{
    [Theory]
    [InlineData("agent_task")]
    [InlineData("spike")]
    [InlineData("historia")]
    [InlineData("tarefa")]
    [InlineData("bug")]
    [InlineData("adr")]
    [InlineData("documento")]
    [InlineData("revisao")]
    [InlineData("incidente")]
    [InlineData("chamado")]
    public void WorkCardWithInstructionAndNotBlockedIsDispatchable(string cardType)
    {
        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts(cardType, HasInstruction: true, IsBlocked: false));

        Assert.True(snapshot.IsDispatchable);
        Assert.Empty(snapshot.Blockers);
    }

    [Theory]
    [InlineData("human_gate")]
    [InlineData("decision")]
    [InlineData("feature")]
    [InlineData("gate")]
    public void ContainerAndDecisionCardTypesAreNeverDispatchable(string cardType)
    {
        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts(cardType, HasInstruction: true, IsBlocked: false));

        Assert.False(snapshot.IsDispatchable);
        Assert.Contains(CardReadinessEvaluator.CardTypeNotDispatchable, snapshot.Blockers);
    }

    [Fact]
    public void MissingInstructionBlocksDispatch()
    {
        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts("agent_task", HasInstruction: false, IsBlocked: false));

        Assert.False(snapshot.IsDispatchable);
        Assert.Contains(CardReadinessEvaluator.InstructionMissing, snapshot.Blockers);
    }

    [Fact]
    public void BlockedCardIsNotDispatchable()
    {
        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts("agent_task", HasInstruction: true, IsBlocked: true));

        Assert.False(snapshot.IsDispatchable);
        Assert.Contains(CardReadinessEvaluator.Blocked, snapshot.Blockers);
    }

    [Fact]
    public void EveryFailingFactContributesItsOwnTypedBlocker()
    {
        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts("human_gate", HasInstruction: false, IsBlocked: true));

        Assert.False(snapshot.IsDispatchable);
        Assert.Equal(
            new[]
            {
                CardReadinessEvaluator.CardTypeNotDispatchable,
                CardReadinessEvaluator.InstructionMissing,
                CardReadinessEvaluator.Blocked,
            },
            snapshot.Blockers);
    }

    // ---- Card Slice Readiness Gate (INC-EVAL-004) -------------------------------------

    /// <summary>
    /// Um card pequeno, bem recortado, com poucos critérios — o caso comum — não é bloqueado.
    /// </summary>
    [Fact]
    public void ASmallWellSlicedCardIsNotBlockedBySize()
    {
        var body =
            "Adicione validação de e-mail no formulário de cadastro.\n\n" +
            "## Critérios de aceite\n" +
            "- Campo e-mail rejeita valores sem @\n" +
            "- Mensagem de erro aparece abaixo do campo\n\n" +
            "Arquivo: `frontend/src/forms/Cadastro.tsx`";

        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts(
                "agent_task", HasInstruction: true, IsBlocked: false, InstructionBody: body));

        Assert.True(snapshot.IsDispatchable);
        Assert.Empty(snapshot.Blockers);
    }

    /// <summary>O card RBAC real (INC-EVAL-004): dezenas de critérios de aceite num card só.</summary>
    [Fact]
    public void TooManyAcceptanceCriteriaBlocksDispatchWithTheCount()
    {
        var criteria = string.Join(
            '\n', Enumerable.Range(1, 11).Select(i => $"- Critério {i} do RBAC"));
        var body = $"Implemente RBAC configurável.\n\n## Critérios de aceite\n{criteria}";

        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts(
                "agent_task", HasInstruction: true, IsBlocked: false, InstructionBody: body));

        Assert.False(snapshot.IsDispatchable);
        Assert.Contains($"{CardReadinessEvaluator.CardTooLarge}:acceptance_criteria:11", snapshot.Blockers);
    }

    [Fact]
    public void TooManyReferencedPathsBlocksDispatchWithTheCount()
    {
        var paths = string.Join(
            '\n', Enumerable.Range(1, 13).Select(i => $"Ajuste `src/Modules/Foo/File{i}.cs`."));
        var body = $"Refatore o módulo inteiro.\n\n{paths}";

        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts(
                "agent_task", HasInstruction: true, IsBlocked: false, InstructionBody: body));

        Assert.False(snapshot.IsDispatchable);
        Assert.Contains($"{CardReadinessEvaluator.CardTooLarge}:referenced_paths:13", snapshot.Blockers);
    }

    [Fact]
    public void AMonolithicBodyWithNoStructureFallsBackToRawLength()
    {
        var body = new string('x', CardReadinessEvaluator.MaxInstructionBodyLength + 1);

        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts(
                "agent_task", HasInstruction: true, IsBlocked: false, InstructionBody: body));

        Assert.False(snapshot.IsDispatchable);
        Assert.Contains(
            $"{CardReadinessEvaluator.CardTooLarge}:body_length:{body.Length}", snapshot.Blockers);
    }

    /// <summary>Corpo ausente nunca é bloqueado por tamanho — quem pega isso é InstructionMissing.</summary>
    [Fact]
    public void AMissingInstructionBodyIsNeverBlockedBySizeItself()
    {
        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts(
                "agent_task", HasInstruction: false, IsBlocked: false, InstructionBody: null));

        Assert.Contains(CardReadinessEvaluator.InstructionMissing, snapshot.Blockers);
        Assert.DoesNotContain(
            snapshot.Blockers, blocker => blocker.StartsWith(CardReadinessEvaluator.CardTooLarge, StringComparison.Ordinal));
    }

    /// <summary>
    /// Listas fora da seção de critérios de aceite (ex.: contexto, passos de investigação) não
    /// contam — o proxy só mede o que o autor declarou como critério.
    /// </summary>
    [Fact]
    public void ListItemsOutsideTheAcceptanceCriteriaSectionDoNotCount()
    {
        var context = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"- nota de contexto {i}"));
        var body = $"## Contexto\n{context}\n\n## Critérios de aceite\n- único critério real";

        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts(
                "agent_task", HasInstruction: true, IsBlocked: false, InstructionBody: body));

        Assert.True(snapshot.IsDispatchable);
    }
}
