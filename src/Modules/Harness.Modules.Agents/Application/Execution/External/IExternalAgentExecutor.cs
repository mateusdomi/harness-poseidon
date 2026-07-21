using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;

namespace Harness.Modules.Agents.Application.Execution.External;

/// <summary>
/// Adapter tipado de um executor externo real (CA-4).
///
/// O ciclo é: `probe` → `start` → `stream` → `collect`, com `cancel`/`stop` a qualquer
/// momento e `cleanup` sempre ao final. Nenhuma flag é inventada: cada adapter usa apenas
/// argumentos e variáveis observados na CLI instalada.
/// </summary>
public interface IExternalAgentExecutor
{
    string ExecutorId { get; }

    ExecutorProfile Profile { get; }

    /// <summary>Saúde OBSERVADA do binário nesta máquina. Ausente é `Unavailable`.</summary>
    Task<ExecutorProbeResult> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inicia a execução e devolve a sessão viva. O prompt vai por stdin; o ambiente do
    /// processo é montado a partir do perfil isolado da conta.
    /// </summary>
    Task<IExternalAgentSession> StartAsync(
        ExternalAgentRunRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sessão viva de um executor externo. `DisposeAsync` sempre encerra o processo e remove
/// os artefatos temporários — nenhum processo órfão sobrevive à sessão.
/// </summary>
public interface IExternalAgentSession : IAsyncDisposable
{
    string RunId { get; }

    /// <summary>Identificador de sessão do executor, conhecido após o primeiro evento.</summary>
    string? SessionId { get; }

    int ProcessId { get; }

    bool IsRunning { get; }

    /// <summary>Eventos sanitizados, na ordem observada.</summary>
    IAsyncEnumerable<ExternalAgentEvent> StreamAsync(CancellationToken cancellationToken = default);

    /// <summary>Aguarda o fim e coleta o resultado. Idempotente.</summary>
    Task<ExternalAgentRunResult> CollectAsync(CancellationToken cancellationToken = default);

    /// <summary>Cancelamento cooperativo: fecha a entrada e aguarda o encerramento.</summary>
    Task CancelAsync(CancellationToken cancellationToken = default);

    /// <summary>Encerramento forçado da ÁRVORE de processos. Não deixa órfão.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Remove os artefatos temporários da execução.</summary>
    Task CleanupAsync(CancellationToken cancellationToken = default);
}
