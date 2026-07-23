using Harness.Modules.Architecture.Contracts;

namespace Harness.Modules.Architecture.Application;

/// <summary>Mapeamentos PUROS entre os fatos do modelo e os contratos de resposta.</summary>
public static class ArchitectureMapper
{
    public static ArchitectureElementContract ToContract(ArchElement element) => new(
        element.Id, element.ProjectId, element.Kind, element.Name, element.Description,
        element.Properties, element.State, element.Locked, element.Version);

    public static ArchitectureRelationshipContract ToContract(ArchRelationship relationship) => new(
        relationship.Id, relationship.ProjectId, relationship.SourceId, relationship.TargetId,
        relationship.Kind, relationship.Properties, relationship.State, relationship.Version);

    public static ArchDocumentRefContract ToContract(ArchDocumentLink link) => new(
        link.Id, link.Title, link.Kind, link.State);
}
