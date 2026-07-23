using Harness.Modules.Architecture.Contracts;
using Harness.Modules.Architecture.Domain;

namespace Harness.Modules.Architecture.Application;

/// <summary>
/// ARC-01 — uma VIEW é uma SELEÇÃO/FILTRO nomeada sobre o modelo (C4/ArchiMate), nunca uma imagem. O
/// mesmo elemento é REUSADO por várias views; a view não copia o elemento, apenas o referencia. Este
/// resolvedor PURO materializa a view como um subgrafo coerente do modelo VIGENTE: elementos
/// selecionados/filtrados + os relacionamentos cujos dois extremos pertencem à seleção.
/// </summary>
public static class ArchitectureViewResolver
{
    public static ArchitectureViewContract Resolve(ArchitectureModelInput model, ArchViewSpec spec)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(spec);

        var implemented = model.Elements
            .Where(e => e.State == ArchitectureKinds.Implemented)
            .ToArray();

        IEnumerable<ArchElement> selected = spec.ElementIds.Count > 0
            ? implemented.Where(e => spec.ElementIds.Contains(e.Id))
            : implemented;

        if (spec.FilterKinds.Count > 0)
        {
            var kinds = new HashSet<string>(spec.FilterKinds, StringComparer.Ordinal);
            selected = selected.Where(e => kinds.Contains(e.Kind));
        }

        if (spec.FilterTags.Count > 0)
        {
            var tags = new HashSet<string>(spec.FilterTags, StringComparer.OrdinalIgnoreCase);
            selected = selected.Where(e => e.Properties.Values.Any(v => tags.Contains(v)));
        }

        var elements = selected
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToArray();
        var elementIds = elements.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var relationshipFilter = spec.RelationshipIds.Count > 0
            ? new HashSet<string>(spec.RelationshipIds, StringComparer.Ordinal)
            : null;

        var relationships = model.Relationships
            .Where(r => r.State == ArchitectureKinds.Implemented &&
                elementIds.Contains(r.SourceId) && elementIds.Contains(r.TargetId) &&
                (relationshipFilter is null || relationshipFilter.Contains(r.Id)))
            .OrderBy(r => r.SourceId, StringComparer.Ordinal)
            .ThenBy(r => r.TargetId, StringComparer.Ordinal)
            .Select(ArchitectureMapper.ToContract)
            .ToArray();

        return new ArchitectureViewContract(
            spec.Id, spec.ProjectId, spec.Name, spec.Description, spec.Notation,
            elements.Select(ArchitectureMapper.ToContract).ToArray(), relationships);
    }
}

/// <summary>Especificação de uma view: seleção explícita e/ou filtros por tipo/tag. Nunca uma imagem.</summary>
public sealed record ArchViewSpec(
    string Id,
    string? ProjectId,
    string Name,
    string Description,
    string Notation,
    IReadOnlyList<string> ElementIds,
    IReadOnlyList<string> RelationshipIds,
    IReadOnlyList<string> FilterKinds,
    IReadOnlyList<string> FilterTags);
