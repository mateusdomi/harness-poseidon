using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.WorkChain;
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
        IPlanMaterializationStore materializations,
        Func<string, string, Task<int>> countOutboxAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(materializations);
        ArgumentNullException.ThrowIfNull(countOutboxAsync);
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

        // Renomear (BUG-03): aplica o novo título, incrementa a versão e persiste — paridade
        // Sqlite/Postgres. Versão desatualizada devolve conflito; id inexistente devolve NotFound.
        var renamed = await conversations.RenameConversationAsync(
            tenantId, conversationId, 1, "Título renomeado", now.AddMilliseconds(5), cancellationToken);
        Assert.Equal(ConversationMutationStatus.Applied, renamed.Status);
        Assert.Equal("Título renomeado", renamed.Conversation!.Title);
        Assert.Equal(2, renamed.Conversation.Version);
        var reread = await conversations.GetConversationAsync(tenantId, conversationId, cancellationToken);
        Assert.Equal("Título renomeado", reread!.Title);
        var staleRename = await conversations.RenameConversationAsync(
            tenantId, conversationId, 1, "Ignorado", now.AddMilliseconds(6), cancellationToken);
        Assert.Equal(ConversationMutationStatus.VersionConflict, staleRename.Status);
        var missingRename = await conversations.RenameConversationAsync(
            tenantId, UlidValue.New(now.AddMilliseconds(7)).ToString(), 1, "Fantasma",
            now.AddMilliseconds(7), cancellationToken);
        Assert.Equal(ConversationMutationStatus.NotFound, missingRename.Status);

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
            now.AddMilliseconds(3),
            new ChiefInvocationSelection(
                "01ARZ3NDEKTSV4RRFFQ69G5FH1", "01ARZ3NDEKTSV4RRFFQ69G5FJ1", "gpt-5",
                "high", "high", ["01ARZ3NDEKTSV4RRFFQ69G5FJ2"], "explicit",
                "Provider-neutral invocation routing.", 0.011m, 100m));
        var enqueued = await chiefTurns.EnqueueAsync(Enqueue(), cancellationToken);
        Assert.Equal(turnId, enqueued.TurnId);
        Assert.Equal(("gpt-5", "high", "explicit"),
            (enqueued.Selection?.ModelName, enqueued.Selection?.Effort, enqueued.Selection?.Source));
        Assert.Equal(0.011m, enqueued.Selection?.EstimatedCostUsd);
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

        // Fase 0A1 (BR-004): a MESMA transação que concluiu o turno deixou o compromisso de
        // materialização durável e o comando na outbox. Sem isso, o turno já apareceria concluído
        // com a demanda sem plano e sem cards — e uma queda aqui perderia o trabalho em silêncio.
        var commitment = await materializations.GetAsync(tenantId, demandId, cancellationToken);
        Assert.NotNull(commitment);
        Assert.Equal(PlanMaterializationStatus.Pending, commitment!.Status);
        Assert.Equal(projectId, commitment.ProjectId);
        Assert.Equal(turnId, commitment.TurnId);
        Assert.Equal(0, commitment.AttemptCount);
        Assert.Null(commitment.PlanId);
        Assert.Null(commitment.CompletedAt);
        // A INTENÇÃO do turno viajou com o compromisso: sem ela, um replanejamento após reinício
        // recomporia o plano com hipóteses diferentes das que a Bruna declarou.
        Assert.Equal(["Critério de paridade dual."], commitment.Request.AcceptanceCriteria);
        Assert.Equal(1, await countOutboxAsync(tenantId, "plan.materializationRequested"));

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

        // FALHA: a política de retentativa é do store, e quem chama precisa saber QUANDO o turno
        // morreu — é o instante em que a pergunta do usuário fica sem resposta e alguém tem de
        // contar isso a ele. Antes o método não devolvia nada e o fato ficava só no mailbox.
        var failingTurnId = UlidValue.New(now.AddMilliseconds(20)).ToString();
        await chiefTurns.EnqueueAsync(
            new ChiefTurnEnqueueCommand(
                tenantId, projectId, conversationId, failingTurnId, chiefAgentId,
                new MessageRecord(
                    tenantId, projectId, UlidValue.New(now.AddMilliseconds(21)).ToString(),
                    conversationId, "user", profileId, null, "Vai falhar", null,
                    now.AddMilliseconds(21)),
                $"idem:{failingTurnId}", now.AddMilliseconds(21)),
            cancellationToken);

        var terminalAt = 0;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var failingLease = await chiefTurns.AcquireNextAsync(
                "parity-worker", now.AddMilliseconds(21 + attempt), TimeSpan.FromMinutes(1),
                cancellationToken);
            if (failingLease is null)
            {
                break;
            }

            var outcome = await chiefTurns.FailAsync(
                new ChiefTurnFailCommand(
                    failingLease, "ParityFailure", now.AddMilliseconds(22 + attempt), true),
                cancellationToken);
            if (outcome.Terminal)
            {
                terminalAt = attempt;
                break;
            }
        }

        // Morre na terceira: as duas primeiras voltam para a fila, a terceira encerra.
        Assert.Equal(3, terminalAt);
        var deadTurn = await chiefTurns.GetAsync(tenantId, failingTurnId, cancellationToken);
        Assert.Equal("failed", deadTurn!.State);
        Assert.Null(await chiefTurns.AcquireNextAsync(
            "parity-worker", now.AddMilliseconds(40), TimeSpan.FromMinutes(1), cancellationToken));

        // O turno morto NÃO emudece o chefe: o agente volta a `idle` e um novo turno é aceito.
        // Enquanto ele ficava em `error`, a prontidão barrava toda mensagem seguinte com
        // `agent.degraded` e o projeto perdia a única voz com o usuário — sem saída sem SQL.
        var revivedTurnId = UlidValue.New(now.AddMilliseconds(50)).ToString();
        await chiefTurns.EnqueueAsync(
            new ChiefTurnEnqueueCommand(
                tenantId, projectId, conversationId, revivedTurnId, chiefAgentId,
                new MessageRecord(
                    tenantId, projectId, UlidValue.New(now.AddMilliseconds(51)).ToString(),
                    conversationId, "user", profileId, null, "Reenviando", null,
                    now.AddMilliseconds(51)),
                $"idem:{revivedTurnId}", now.AddMilliseconds(51)),
            cancellationToken);
        var revivedLease = await chiefTurns.AcquireNextAsync(
            "parity-worker", now.AddMilliseconds(52), TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(revivedLease);
        Assert.Equal(revivedTurnId, revivedLease!.Turn.TurnId);
    }
}
