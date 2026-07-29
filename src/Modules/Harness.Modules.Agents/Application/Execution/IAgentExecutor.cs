namespace Harness.Modules.Agents.Application.Execution;

public interface IAgentExecutor
{
    Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AgentExecutionRequest(
    string TenantId,
    string ProjectId,
    string ConversationId,
    string AgentId,
    string Instruction,
    string StatusDigestJson,
    string WorkingDirectory,
    string? SessionId = null,
    string? Model = null,
    string? Effort = null,
    string? CommunicationInstructions = null,
    IReadOnlyList<AgentSpecialistOption>? Specialists = null,
    ChiefCommunicationContext? CommunicationContext = null);

/// <summary>
/// Uma opção do catálogo de especialistas apresentada ao Chefe para que ele possa DELEGAR a quem
/// é qualificado. A persona do Chefe manda "delegue ao especialista cuja persona melhor encaixa" —
/// sem o catálogo em mãos, essa instrução era irrealizável e a escolha caía numa heurística de
/// palavra-chave que alcançava 5 das 25 personas semeadas. É contexto MÍNIMO (chave, nome,
/// especialidade), nunca a persona inteira.
/// </summary>
public sealed record AgentSpecialistOption(string Key, string Name, string? Specialty);

public sealed record AgentExecutionResult(
    string Executor,
    string SessionId,
    string TurnId,
    string StructuredOutput,
    IReadOnlyList<string> Chunks,
    long DurationMs);
