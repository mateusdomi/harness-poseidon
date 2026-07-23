namespace Harness.Modules.Delivery.Contracts;

// DEL-03 — Daily Copilot (Tech Lead). Um copiloto para a daily que NÃO cria nem atualiza cards de PO:
// deriva o briefing pré-daily dos dados de entrega já existentes (Projeto 360 / sinais de atenção /
// previsão honesta), captura marcações TIPADAS durante a daily (persistidas de forma durável e
// append-only) e compõe o resumo pós-daily. Nada aqui inventa números — tudo deriva de fatos gravados.

/// <summary>As marcações tipadas que podem ser capturadas durante a daily (DEL-03).</summary>
public static class DailyCaptureKinds
{
    public const string Access = "access";
    public const string Dependency = "dependency";
    public const string Decision = "decision";
    public const string Deadline = "deadline";
    public const string Scope = "scope";
    public const string Doc = "doc";
    public const string Risk = "risk";

    public static readonly IReadOnlyList<string> All =
        [Access, Dependency, Decision, Deadline, Scope, Doc, Risk];

    public static bool IsValid(string? kind) =>
        kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>Uma mudança relevante desde a última daily, derivada dos fatos (não inventada).</summary>
public sealed record DailyChangeContract(string Code, string Detail);

/// <summary>Uma pergunta recomendada para a daily, derivada de um sinal concreto.</summary>
public sealed record DailyQuestionContract(string Topic, string Question);

/// <summary>Foto compacta da saúde/previsão da entrega, reusada no briefing e no resumo.</summary>
public sealed record DailySnapshotContract(
    string Health,
    string Predictability,
    int OpenTaskCount,
    int BlockedTaskCount,
    int MilestonesTotal,
    int MilestonesDone,
    DeliveryForecastContract Forecast);

/// <summary>
/// Briefing pré-daily (DEL-03): o que mudou desde a última daily, o que precisa de atenção e as
/// perguntas recomendadas — tudo derivado dos dados de entrega. Quando não há daily anterior,
/// <see cref="IsFirstDaily"/> é verdadeiro e as mudanças refletem o estado inicial observável.
/// </summary>
public sealed record DailyBriefingContract(
    string DeliveryId,
    DateTimeOffset GeneratedAt,
    bool IsFirstDaily,
    DateTimeOffset? LastDailyAt,
    DailySnapshotContract Snapshot,
    IReadOnlyList<DailyChangeContract> ChangesSinceLast,
    IReadOnlyList<AttentionSignalContract> ItemsNeedingAttention,
    IReadOnlyList<DailyQuestionContract> RecommendedQuestions);

/// <summary>Pedido para capturar uma marcação tipada durante a daily (DEL-03).</summary>
public sealed record DailyCaptureRequest(string Kind, string Note, string? CapturedBy);

/// <summary>Uma marcação capturada e persistida de forma durável (append-only).</summary>
public sealed record DailyCaptureContract(
    string Id,
    string DeliveryId,
    string Kind,
    string Note,
    string CapturedBy,
    DateTimeOffset CreatedAt);

/// <summary>Contagem de marcações por tipo no resumo pós-daily.</summary>
public sealed record DailyCaptureKindCountContract(string Kind, int Count);

/// <summary>
/// Resumo pós-daily (DEL-03): as marcações capturadas na sessão agrupadas por tipo, mais a foto de
/// saúde/previsão. <see cref="CreatedPoCards"/> é sempre <c>false</c> — por padrão, o copiloto NÃO
/// cria nem atualiza cards de PO; ele apenas registra e resume.
/// </summary>
public sealed record DailySummaryContract(
    string DeliveryId,
    DateTimeOffset GeneratedAt,
    DateTimeOffset? SessionSince,
    int TotalCaptures,
    IReadOnlyList<DailyCaptureKindCountContract> ByKind,
    IReadOnlyList<DailyCaptureContract> Captures,
    DailySnapshotContract Snapshot,
    bool CreatedPoCards);
