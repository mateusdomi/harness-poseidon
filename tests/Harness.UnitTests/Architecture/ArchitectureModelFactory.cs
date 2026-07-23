using Harness.Modules.Architecture.Application;

namespace Harness.UnitTests.Architecture;

/// <summary>Fábrica de fatos do modelo arquitetural para os testes puros (ARC-01/02/03/05).</summary>
internal static class ArchitectureModelFactory
{
    public static ArchElement Element(
        string id, string kind, string name, string state = "implemented", bool locked = false,
        string? proposalId = null, string? counterpartId = null, string? changeKind = null,
        IReadOnlyDictionary<string, string>? properties = null, string description = "") => new(
        id, "proj", kind, name, description, properties ?? new Dictionary<string, string>(),
        state, locked, 1, proposalId, counterpartId, changeKind);

    public static ArchRelationship Relationship(
        string id, string source, string target, string kind, string state = "implemented",
        string? proposalId = null, string? counterpartId = null, string? changeKind = null) => new(
        id, "proj", source, target, kind, new Dictionary<string, string>(), state, 1,
        proposalId, counterpartId, changeKind);

    public static ArchSystemMetadata Sys(
        string elementId, string criticality = "medium", string? domain = null, string? owner = "owner",
        IReadOnlyList<string>? capabilities = null, string? lifecycleStatus = null,
        decimal? cost = null, int incidents = 0, int? busFactor = null, string? duplicateOf = null,
        string? risk = null, IReadOnlyList<ArchDocumentLink>? documents = null) => new(
        elementId, domain, capabilities ?? [], owner, criticality, ["dotnet"], lifecycleStatus, cost,
        incidents, busFactor, duplicateOf, risk, "99.9%", "daily", "warm-standby", null, false, false,
        "1y", [], documents ?? [], 0, [], null, null);
}
