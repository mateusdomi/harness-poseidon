using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Conversations;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Persistence;

/// <summary>
/// Cenário provider-neutro do chat + pipeline durável do Chief: conversa,
/// enfileiramento idempotente por turn id, lease com fencing, conclusão
/// transacional com materialização de demanda e replay sem duplicação.
/// </summary>
public static class ConversationChiefStoreBehavior
{
    public static async Task AssertAsync(
        IConversationStore conversations,
        IChiefTurnStore chiefTurns,
        string tenantId,
        string projectId,
        string profileId,
        string chiefAgentId,
        Func<string, Task<int>> countDemandsAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(countDemandsAsync);
        var now = DateTimeOffset.UtcNow;

        // Conversa criada e listável no projeto.
        var conversationId = UlidValue.New(now).ToString();
        var created = await conversations.CreateConversationAsync(
            new ConversationCreateCommand(
                new ConversationRecord(
                    tenantId, conversationId, projectId, "Paridade dual", "active",
                    profileId, now, null, 1),
                now),
            cancellationToken);
        Assert.Equal(ConversationMutationStatus.Applied, created.Status);
        Assert.NotNull(
            await conversations.GetConversationAsync(tenantId, conversationId, cancellationToken));

        // Enfileiramento idempotente por turn id: replay devolve o mesmo turno.
        var turnId = UlidValue.New(now.AddMilliseconds(1)).ToString();
        var userMessageId = UlidValue.New(now.AddMilliseconds(2)).ToString();
        ChiefTurnEnqueueCommand Enqueue() => new(
            tenantId,
            projectId,
            conversationId,
            turnId,
            chiefAgentId,
            new MessageRecord(
                tenantId, projectId, userMessageId, conversationId, "user",
                profileId, null, "Planeje a entrega de paridade.", null,
                now.AddMilliseconds(2)),
            $"chief-turn:{turnId}",
            now.AddMilliseconds(3));
        var enqueued = await chiefTurns.EnqueueAsync(Enqueue(), cancellationToken);
        Assert.Equal(turnId, enqueued.TurnId);
        var replayed = await chiefTurns.EnqueueAsync(Enqueue(), cancellationToken);
        Assert.Equal(turnId, replayed.TurnId);
        Assert.Single(
            await conversations.ListMessagesAsync(
                tenantId, conversationId, null, 50, cancellationToken));

        // Lease adquirida com fencing; conclusão transacional materializa a demanda.
        var lease = await chiefTurns.AcquireNextAsync(
            "parity-worker", now.AddMilliseconds(4), TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(lease);
        Assert.Equal(turnId, lease!.Turn.TurnId);
        var demandsBefore = await countDemandsAsync(tenantId);
        var chiefMessageAt = now.AddMilliseconds(5);
        var demandId = UlidValue.New(now.AddMilliseconds(6)).ToString();
        await chiefTurns.CompleteAsync(
            new ChiefTurnCompleteCommand(
                lease,
                new MessageRecord(
                    tenantId, projectId, UlidValue.New(chiefMessageAt).ToString(),
                    conversationId, "chief", null, chiefAgentId,
                    "Planejado; demanda registrada.", null, chiefMessageAt),
                ["Planejado; ", "demanda registrada."],
                "session-parity",
                """{"phase":"parity"}""",
                chiefMessageAt,
                [
                    new ChiefDemandSeed(
                        demandId,
                        UlidValue.New(now.AddMilliseconds(7)).ToString(),
                        "Demanda de paridade",
                        "Materializada na conclusão do turno em ambos os providers.",
                        "medium",
                        ["Critério de paridade dual."]),
                ]),
            cancellationToken);
        Assert.Equal(demandsBefore + 1, await countDemandsAsync(tenantId));
        var completedTurn = await chiefTurns.GetAsync(tenantId, turnId, cancellationToken);
        Assert.Equal("completed", completedTurn!.State);
        Assert.Equal(2, (await conversations.ListMessagesAsync(
            tenantId, conversationId, null, 50, cancellationToken)).Count);

        // Sem pendências: próxima aquisição é nula; lease antigo não conclui de novo.
        Assert.Null(await chiefTurns.AcquireNextAsync(
            "parity-worker", now.AddMilliseconds(8), TimeSpan.FromMinutes(1), cancellationToken));
        await Assert.ThrowsAsync<ChiefTurnConflictException>(() => chiefTurns.CompleteAsync(
            new ChiefTurnCompleteCommand(
                lease,
                new MessageRecord(
                    tenantId, projectId, UlidValue.New(now.AddMilliseconds(9)).ToString(),
                    conversationId, "chief", null, chiefAgentId, "Duplicado?", null,
                    now.AddMilliseconds(9)),
                ["Duplicado?"],
                "session-parity",
                """{"phase":"parity"}""",
                now.AddMilliseconds(9)),
            cancellationToken));
    }
}
