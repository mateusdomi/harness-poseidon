using Harness.Modules.Conversations.Contracts;
using Harness.Modules.Conversations.Domain;

namespace Harness.Modules.Conversations.Application;

public static class ConversationApplicationService
{
    public static ConversationContract Create(
        string id,
        string profileId,
        CreateConversationRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ToContract(Conversation.Create(id, request.ProjectId, request.Title, profileId, now));
    }

    public static MessageContract CreateUserMessage(
        string id,
        string profileId,
        CreateMessageRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ToContract(ConversationMessage.Create(
            id,
            request.ConversationId,
            "user",
            profileId,
            null,
            request.Content,
            null,
            now));
    }

    public static MessageContract CreateChiefMessage(
        string id,
        string conversationId,
        string agentId,
        string content,
        DateTimeOffset now) =>
        ToContract(ConversationMessage.Create(
            id,
            conversationId,
            "chief",
            null,
            agentId,
            content,
            CountTokens(content),
            now));

    /// <summary>
    /// Confirmação determinística de recebimento.
    ///
    /// NÃO devolve a mensagem do usuário. Ecoar o texto recebido parece inofensivo e não é: se ele
    /// escrever "adicionar o endpoint /status", a confirmação passa a conter vocabulário técnico
    /// que a experiência de negócio proíbe — e a política de comunicação, corretamente, recusa a
    /// própria resposta do sistema. Confirmar o recebimento não exige repetir o conteúdo.
    /// </summary>
    public static string[] ComposeDeterministicReply(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        return
        [
            "Recebi sua mensagem. ",
            "Ela ficou registrada com segurança e já estou olhando o que ela pede. " +
            "Volto assim que tiver o próximo passo.",
        ];
    }

    private static int CountTokens(string content) =>
        content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static ConversationContract ToContract(Conversation value) =>
        new(value.Id, value.ProjectId, value.Title, value.State, value.CreatedByProfileId,
            value.CreatedAt, value.LastMessageAt, value.Version);

    private static MessageContract ToContract(ConversationMessage value) =>
        new(value.Id, value.ConversationId, value.AuthorRole, value.AuthorProfileId,
            value.AuthorAgentId, value.Content, value.TokenCount, value.CreatedAt);
}
