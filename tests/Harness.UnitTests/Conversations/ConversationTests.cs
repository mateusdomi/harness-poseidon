using Harness.Modules.Conversations.Application;
using Harness.Modules.Conversations.Contracts;
using Harness.Modules.Conversations.Domain;

namespace Harness.UnitTests.Conversations;

public sealed class ConversationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ConversationNormalizesTitleAndRejectsTurnsAfterArchive()
    {
        var conversation = Conversation.Create(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            "  Planejamento principal  ",
            "01ARZ3NDEKTSV4RRFFQ69G5FAX",
            Now);

        Assert.Equal("Planejamento principal", conversation.Title);
        Assert.Equal("active", conversation.State);
        Assert.Null(conversation.LastMessageAt);
        conversation.EnsureTurnCanStart();

        var archived = conversation.Archive(Now.AddMinutes(1));
        Assert.Equal("archived", archived.State);
        Assert.Equal(2, archived.Version);
        Assert.Throws<InvalidOperationException>(archived.EnsureTurnCanStart);
    }

    [Fact]
    public void MessageAuthorityMatchesRoleAndReplyIsDeterministic()
    {
        var user = ConversationApplicationService.CreateUserMessage(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            new CreateMessageRequest(
                "01ARZ3NDEKTSV4RRFFQ69G5FAX",
                "  Prossiga com o plano  "),
            Now);
        var first = ConversationApplicationService.ComposeDeterministicReply(user.Content);
        var replay = ConversationApplicationService.ComposeDeterministicReply(user.Content);

        Assert.Equal("user", user.AuthorRole);
        Assert.NotNull(user.AuthorProfileId);
        Assert.Null(user.AuthorAgentId);
        Assert.Equal("Prossiga com o plano", user.Content);
        Assert.Equal(first, replay);
        Assert.NotEmpty(string.Concat(first));
        Assert.Throws<ArgumentException>(() => ConversationMessage.Create(
            "01ARZ3NDEKTSV4RRFFQ69G5FAY",
            user.ConversationId,
            "chief",
            user.AuthorProfileId,
            null,
            "Resposta inválida",
            1,
            Now));
    }
}
