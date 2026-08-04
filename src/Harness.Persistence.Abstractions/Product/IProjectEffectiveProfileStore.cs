namespace Harness.Persistence.Abstractions.Product;

/// <summary>
/// Uma versão do perfil efetivo, como persistida. O conteúdo tipado viaja como JSON versionado:
/// o perfil evolui (novas áreas, novas modalidades) e transformar cada campo em coluna tornaria
/// cada evolução uma migração de schema. Os campos promovidos a coluna são só os que precisam ser
/// consultados sem desserializar: versão, fingerprint, baseline e modalidade.
/// </summary>
public sealed record ProjectEffectiveProfileRecord(
    string TenantId,
    string ProjectId,
    int Version,
    string Fingerprint,
    string BaselineVersion,
    string Modality,
    string ProfileJson,
    DateTimeOffset ResolvedAt,
    string ResolvedBy,
    string Status);

public sealed record ProjectEffectiveProfileSaveCommand(
    string TenantId,
    string ProjectId,
    string Fingerprint,
    string BaselineVersion,
    string Modality,
    string ProfileJson,
    DateTimeOffset ResolvedAt,
    string ResolvedBy);

/// <summary>
/// Resultado de gravar um perfil. <see cref="Created"/> falso significa que a resolução produziu
/// exatamente o mesmo perfil que já estava vigente — não há decisão nova a registrar, e criar uma
/// versão idêntica só sujaria o histórico.
/// </summary>
public sealed record ProjectEffectiveProfileSaveResult(
    ProjectEffectiveProfileRecord Profile,
    bool Created);

/// <summary>
/// Histórico APPEND-ONLY do perfil efetivo por projeto.
///
/// Nunca sobrescreve: uma decisão arquitetural aprovada é auditável, e a pergunta "qual perfil
/// estava vigente quando este card executou?" precisa ter resposta em dezembro sobre um card de
/// agosto. Cada resolução que muda alguma coisa vira uma versão nova; a anterior permanece.
/// </summary>
public interface IProjectEffectiveProfileStore
{
    /// <summary>Grava uma nova versão, ou devolve a vigente quando o conteúdo é idêntico.</summary>
    Task<ProjectEffectiveProfileSaveResult> SaveAsync(
        ProjectEffectiveProfileSaveCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>A versão vigente, ou <see langword="null"/> quando o projeto ainda não tem perfil.</summary>
    Task<ProjectEffectiveProfileRecord?> GetCurrentAsync(
        string tenantId,
        string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>Uma versão específica — é assim que um card antigo continua auditável.</summary>
    Task<ProjectEffectiveProfileRecord?> GetVersionAsync(
        string tenantId,
        string projectId,
        int version,
        CancellationToken cancellationToken = default);

    /// <summary>O histórico completo, da versão mais recente para a mais antiga.</summary>
    Task<IReadOnlyList<ProjectEffectiveProfileRecord>> ListAsync(
        string tenantId,
        string projectId,
        CancellationToken cancellationToken = default);
}
