namespace Harness.Modules.Projects.Contracts;

/// <summary>
/// CAT-07 — um item do read-model de "atividade recente" de um projeto. Cada item é DERIVADO
/// estritamente de dados duráveis já gravados (demandas, tarefas, solicitações, tentativas de
/// trabalho, previsões de entrega): nada é inventado. O par (<see cref="EntityKind"/>,
/// <see cref="EntityId"/>) aponta para a linha de origem, e <see cref="Summary"/> é uma frase
/// humanizada e determinística sobre o que aconteceu.
/// </summary>
public sealed record ProjectActivityItem(
    string Kind,
    string Summary,
    DateTimeOffset OccurredAt,
    string EntityKind,
    string EntityId,
    string? State,
    string? AgentId);

/// <summary>
/// Página de atividade, ordenada do mais recente para o mais antigo. <see cref="NextCursor"/> é
/// opaco e nulo quando não há mais itens.
/// </summary>
public sealed record ProjectActivityPage(
    string ProjectId,
    IReadOnlyList<ProjectActivityItem> Items,
    string? NextCursor,
    int Count);
