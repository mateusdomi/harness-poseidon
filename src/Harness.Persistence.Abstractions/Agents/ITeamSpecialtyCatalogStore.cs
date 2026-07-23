namespace Harness.Persistence.Abstractions.Agents;

/// <summary>
/// Catálogo real e tenant-scoped de equipes (teams) e especialidades (specialties).
/// Substitui o texto livre que vivia nas colunas <c>team</c>/<c>specialty</c> de
/// <c>agent_definitions</c> por entradas de catálogo com chave estável e nome único por
/// tenant. Uma especialidade pode, opcionalmente, pertencer a uma equipe. Apagar uma
/// entrada referenciada (por outra especialidade ou por uma definição de agente) falha
/// com um conflito tipado.
/// </summary>
public interface ITeamSpecialtyCatalogStore
{
    Task<IReadOnlyList<TeamRecord>> ListTeamsAsync(
        string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<TeamRecord?> GetTeamAsync(
        string tenantId, string teamId, CancellationToken cancellationToken = default);
    Task<TeamRecord> CreateTeamAsync(TeamCreateCommand command, CancellationToken cancellationToken = default);
    Task<TeamRecord> UpdateTeamAsync(TeamUpdateCommand command, CancellationToken cancellationToken = default);
    Task DeleteTeamAsync(TeamDeleteCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SpecialtyRecord>> ListSpecialtiesAsync(
        string tenantId, string? teamId, string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<SpecialtyRecord?> GetSpecialtyAsync(
        string tenantId, string specialtyId, CancellationToken cancellationToken = default);
    Task<SpecialtyRecord> CreateSpecialtyAsync(SpecialtyCreateCommand command, CancellationToken cancellationToken = default);
    Task<SpecialtyRecord> UpdateSpecialtyAsync(SpecialtyUpdateCommand command, CancellationToken cancellationToken = default);
    Task DeleteSpecialtyAsync(SpecialtyDeleteCommand command, CancellationToken cancellationToken = default);
}

public sealed record TeamRecord(
    string Id, string TenantId, string Key, string Name, string? Description,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record SpecialtyRecord(
    string Id, string TenantId, string Key, string Name, string? Description, string? TeamId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record TeamCreateCommand(
    string TenantId, string ActorProfileId, string Id, string? Key, string Name, string? Description,
    DateTimeOffset OccurredAt);
public sealed record TeamUpdateCommand(
    string TenantId, string ActorProfileId, string Id, string? Key, string Name, string? Description,
    DateTimeOffset OccurredAt);
public sealed record TeamDeleteCommand(
    string TenantId, string ActorProfileId, string Id, DateTimeOffset OccurredAt);

public sealed record SpecialtyCreateCommand(
    string TenantId, string ActorProfileId, string Id, string? Key, string Name, string? Description,
    string? TeamId, DateTimeOffset OccurredAt);
public sealed record SpecialtyUpdateCommand(
    string TenantId, string ActorProfileId, string Id, string? Key, string Name, string? Description,
    string? TeamId, DateTimeOffset OccurredAt);
public sealed record SpecialtyDeleteCommand(
    string TenantId, string ActorProfileId, string Id, DateTimeOffset OccurredAt);

public sealed class TeamSpecialtyCatalogNotFoundException(string resource) : Exception(resource)
{
    public string Resource { get; } = resource;
}
public sealed class TeamSpecialtyCatalogValidationException(string detail) : Exception(detail);
public sealed class TeamSpecialtyCatalogConflictException(string detail) : Exception(detail);
