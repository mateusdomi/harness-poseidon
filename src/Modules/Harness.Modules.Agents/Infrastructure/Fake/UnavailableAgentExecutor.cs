using Harness.Modules.Agents.Application.Execution;

namespace Harness.Modules.Agents.Infrastructure.Fake;

/// <summary>
/// Executor fail-closed usado quando nenhum executor real está configurado e o modo simulado
/// não foi explicitamente ligado (ADR-019). Ele nunca produz texto: fingir uma resposta de
/// modelo seria apresentar simulação como execução real. Falhar aqui mantém o turno auditável
/// como falha honesta em vez de entregar uma confirmação de transporte disfarçada de resposta.
/// </summary>
public sealed class UnavailableAgentExecutor : IAgentExecutor
{
    public Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw new AgentExecutorUnavailableException(
            "Nenhum executor de agente real está configurado e o modo simulado não está ligado.");
    }
}

/// <summary>Sinaliza ausência de executor apto; nunca carrega segredo ou conteúdo de prompt.</summary>
public sealed class AgentExecutorUnavailableException(string detail) : Exception(detail);
