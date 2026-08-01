namespace Harness.Persistence.Abstractions.Agents;

/// <summary>
/// B14 (Fase 2B) — o registro do turno da chefe POR INTENÇÃO.
///
/// O turno é o laço mais quente do produto e era o único caminho de execução sem nenhum registro
/// de custo ou duração: `model_invocations` cobre os runs de especialista, e o turno da chefe não
/// escrevia lá nem em lugar nenhum. O painel de produtividade media tudo menos a peça que mais
/// executa.
/// </summary>
public interface IChiefTurnIntentStore
{
    /// <summary>
    /// Registra o desfecho do turno. Idempotente por <c>(tenantId, turnId)</c>: o worker pode
    /// reprocessar um turno após queda sem duplicar a medição — duplicata inflaria a contagem da
    /// intenção e faria parecer que o dono conversa mais do que conversa.
    /// </summary>
    Task RecordAsync(ChiefTurnIntentRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turnos do projeto, do mais recente para o mais antigo, limitados a <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<ChiefTurnIntentRecord>> ListByProjectAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default);
}

/// <param name="DemandsDropped">
/// Quantas demandas a rota da intenção descartou. Zero é o caso saudável; corte frequente numa
/// intenção significa ou modelo classificando mal, ou rota apertada demais — e as duas hipóteses
/// se distinguem olhando a série.
/// </param>
public sealed record ChiefTurnIntentRecord(
    string TenantId,
    string ProjectId,
    string TurnId,
    string Intent,
    double Confidence,
    int DemandsDropped,
    int TeamActionsDropped,
    long DurationMs,
    DateTimeOffset OccurredAt);
