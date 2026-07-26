namespace Harness.Modules.Coordination.Application;

public sealed record CardDependencyNode(
    string CardId,
    IReadOnlyList<string> Provides,
    IReadOnlyList<string> Consumes);

public sealed record CardDependencyEdge(
    string ProviderCardId,
    string ConsumerCardId,
    string Resource);

public sealed record CardDependencyBarrier(
    string ConsumerCardId,
    IReadOnlyList<string> ProviderCardIds);

public sealed record CardDependencyIssue(
    string Code,
    string? CardId,
    string? Resource,
    IReadOnlyList<string> RelatedCardIds);

public sealed record CardDependencyPlan(
    IReadOnlyList<CardDependencyEdge> Edges,
    IReadOnlyList<IReadOnlyList<string>> DispatchWaves,
    IReadOnlyList<CardDependencyBarrier> FanInBarriers,
    IReadOnlyList<CardDependencyIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;

    public IReadOnlyList<string> GetReadyCards(IReadOnlySet<string> completedCardIds)
    {
        ArgumentNullException.ThrowIfNull(completedCardIds);
        if (!IsValid)
        {
            return [];
        }

        var allCards = DispatchWaves.SelectMany(wave => wave);
        var dependencies = Edges
            .GroupBy(edge => edge.ConsumerCardId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.ProviderCardId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        return allCards
            .Where(cardId => !completedCardIds.Contains(cardId))
            .Where(cardId =>
                !dependencies.TryGetValue(cardId, out var providers) ||
                providers.All(completedCardIds.Contains))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}

public static class CardDependencyGraph
{
    public static CardDependencyPlan Build(
        IReadOnlyList<CardDependencyNode> nodes,
        IReadOnlyCollection<string>? externalResources = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        externalResources ??= [];

        var normalizedNodes = nodes.Select(Normalize).ToArray();
        var duplicateCard = normalizedNodes
            .GroupBy(node => node.CardId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateCard is not null)
        {
            throw new ArgumentException(
                $"Card id '{duplicateCard.Key}' is duplicated.",
                nameof(nodes));
        }

        var external = externalResources
            .Select(resource => NormalizeResource(resource, nameof(externalResources)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var providers = normalizedNodes
            .SelectMany(node => node.Provides.Select(resource => (node.CardId, Resource: resource)))
            .GroupBy(item => item.Resource, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.CardId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var issues = new List<CardDependencyIssue>();
        foreach (var (resource, providerCards) in providers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (providerCards.Length > 1)
            {
                issues.Add(new CardDependencyIssue(
                    "ambiguous_resource_provider",
                    null,
                    resource,
                    providerCards));
            }
        }

        var edges = new List<CardDependencyEdge>();
        foreach (var node in normalizedNodes.OrderBy(node => node.CardId, StringComparer.Ordinal))
        {
            foreach (var resource in node.Consumes)
            {
                if (providers.TryGetValue(resource, out var providerCards))
                {
                    if (providerCards.Length == 1)
                    {
                        edges.Add(new CardDependencyEdge(providerCards[0], node.CardId, resource));
                    }

                    continue;
                }

                if (!external.Contains(resource))
                {
                    issues.Add(new CardDependencyIssue(
                        "missing_resource_provider",
                        node.CardId,
                        resource,
                        []));
                }
            }
        }

        var distinctEdges = edges
            .DistinctBy(
                edge => (edge.ProviderCardId, edge.ConsumerCardId, edge.Resource),
                CardDependencyEdgeKeyComparer.Instance)
            .OrderBy(edge => edge.ProviderCardId, StringComparer.Ordinal)
            .ThenBy(edge => edge.ConsumerCardId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Resource, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var waves = BuildWaves(normalizedNodes, distinctEdges, issues);
        var barriers = distinctEdges
            .GroupBy(edge => edge.ConsumerCardId, StringComparer.Ordinal)
            .Select(group => new CardDependencyBarrier(
                group.Key,
                group.Select(edge => edge.ProviderCardId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray()))
            .Where(barrier => barrier.ProviderCardIds.Count > 1)
            .OrderBy(barrier => barrier.ConsumerCardId, StringComparer.Ordinal)
            .ToArray();

        return new CardDependencyPlan(
            distinctEdges,
            waves,
            barriers,
            issues
                .OrderBy(issue => issue.Code, StringComparer.Ordinal)
                .ThenBy(issue => issue.CardId, StringComparer.Ordinal)
                .ThenBy(issue => issue.Resource, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static List<IReadOnlyList<string>> BuildWaves(
        IReadOnlyList<NormalizedNode> nodes,
        IReadOnlyList<CardDependencyEdge> edges,
        List<CardDependencyIssue> issues)
    {
        var cardIds = nodes.Select(node => node.CardId).ToHashSet(StringComparer.Ordinal);
        var indegree = cardIds.ToDictionary(cardId => cardId, _ => 0, StringComparer.Ordinal);
        var consumers = cardIds.ToDictionary(
            cardId => cardId,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var dependency in edges
                     .Select(edge => (edge.ProviderCardId, edge.ConsumerCardId))
                     .Distinct())
        {
            if (consumers[dependency.ProviderCardId].Add(dependency.ConsumerCardId))
            {
                indegree[dependency.ConsumerCardId]++;
            }
        }

        var waves = new List<IReadOnlyList<string>>();
        var ready = indegree
            .Where(pair => pair.Value == 0)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var visited = 0;
        while (ready.Length > 0)
        {
            waves.Add(ready);
            visited += ready.Length;
            var next = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var provider in ready)
            {
                foreach (var consumer in consumers[provider])
                {
                    indegree[consumer]--;
                    if (indegree[consumer] == 0)
                    {
                        next.Add(consumer);
                    }
                }
            }

            ready = [.. next];
        }

        if (visited != cardIds.Count)
        {
            var cyclicCards = indegree
                .Where(pair => pair.Value > 0)
                .Select(pair => pair.Key)
                .Order(StringComparer.Ordinal)
                .ToArray();
            issues.Add(new CardDependencyIssue(
                "dependency_cycle",
                null,
                null,
                cyclicCards));
            return [];
        }

        return waves;
    }

    private static NormalizedNode Normalize(CardDependencyNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(node.CardId);
        ArgumentNullException.ThrowIfNull(node.Provides);
        ArgumentNullException.ThrowIfNull(node.Consumes);

        return new NormalizedNode(
            node.CardId.Trim(),
            node.Provides
                .Select(resource => NormalizeResource(resource, nameof(node.Provides)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            node.Consumes
                .Select(resource => NormalizeResource(resource, nameof(node.Consumes)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static string NormalizeResource(string resource, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource, parameterName);
        var normalized = resource.Trim();
        if (normalized.Length > 200 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Dependency resource references are limited to 200 printable characters.",
                parameterName);
        }

        return normalized;
    }

    private sealed record NormalizedNode(
        string CardId,
        IReadOnlyList<string> Provides,
        IReadOnlyList<string> Consumes);

    private sealed class CardDependencyEdgeKeyComparer
        : IEqualityComparer<(string ProviderCardId, string ConsumerCardId, string Resource)>
    {
        public static CardDependencyEdgeKeyComparer Instance { get; } = new();

        public bool Equals(
            (string ProviderCardId, string ConsumerCardId, string Resource) x,
            (string ProviderCardId, string ConsumerCardId, string Resource) y) =>
            string.Equals(x.ProviderCardId, y.ProviderCardId, StringComparison.Ordinal) &&
            string.Equals(x.ConsumerCardId, y.ConsumerCardId, StringComparison.Ordinal) &&
            string.Equals(x.Resource, y.Resource, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(
            (string ProviderCardId, string ConsumerCardId, string Resource) value) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(value.ProviderCardId),
                StringComparer.Ordinal.GetHashCode(value.ConsumerCardId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Resource));
    }
}
