using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Harness.Modules.Governance.Documentation;
using Harness.SharedKernel.Security;

namespace Harness.Modules.Governance.Context;

public enum ContextSegmentKind
{
    Core,
    Organization,
    Project,
    WorkflowPhase,
    Persona,
    Skill,
    PathConstraint,
    StatusDigest,
    AcceptanceCriteria,
    ToolPermission,
    Evidence,
    StopCondition,
    Budget,
    Memory,
}

public sealed record ContextMemorySlice(
    string DocumentId,
    string Content,
    string CitationReference,
    int EstimatedTokens);

/// <summary>
/// Fatia de SKILL PROMOVIDA — uma instrução que o ciclo de aprendizado aprovou e promoveu, e que
/// passa a valer para as próximas execuções.
///
/// Existia o caminho inteiro até <c>Promoted</c> — proposta, avaliação, sombra, aprovação — e
/// nenhum leitor: a skill era promovida e ficava no banco, sem nunca chegar a um agente. Esta
/// fatia é o consumo.
///
/// <c>PathScopes</c> é o ESCOPO: vazio significa "vale para o projeto todo"; com
/// caminhos, a skill só entra no bundle de um card que trabalhe dentro deles. Uma skill aprendida
/// consertando o frontend não deve reaparecer como instrução num card de migração de banco.
/// </summary>
public sealed record ContextSkillSlice(
    string SkillId,
    string Title,
    string Instructions,
    IReadOnlyList<string> PathScopes,
    string CitationReference,
    int EstimatedTokens);

/// <summary>
/// A PERSONA que vai executar o card (Fase 2A.3).
///
/// <see cref="ContextSegmentKind.Persona"/> existia no enum desde o começo e nunca teve produtor:
/// nenhuma persona chegava ao prompt. O agente recebia papel, escopo e critério de aceite — e
/// nenhuma palavra sobre COMO aquela especialidade pensa, o que ela entrega e onde ela para. O
/// resultado é um executor genérico com crachá de especialista.
///
/// Os campos são os que mudam a decisão de quem executa: mentalidade, missão, princípios,
/// entregáveis e limites. Ficam de fora deliberadamente os critérios de acionamento — eles servem
/// à CHEFE, para escolher a persona; repeti-los ao agente já escolhido é gastar orçamento de
/// contexto dizendo por que ele foi chamado.
/// </summary>
public sealed record ContextPersonaSlice(
    string Key,
    string Name,
    string? Mindset,
    string? Mission,
    IReadOnlyList<string> OperatingPrinciples,
    IReadOnlyList<string> Deliverables,
    IReadOnlyList<string> Limitations);

public sealed record ContextBundleRequest(
    string TenantId,
    string ProjectId,
    string TaskId,
    string AttemptId,
    string AgentId,
    string Provider,
    string? Model,
    string Workflow,
    string Phase,
    string TaskType,
    string RiskTier,
    IReadOnlyList<string> Paths,
    string StatusDigestJson,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> ToolPermissions,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> StopConditions,
    int TokenBudget,
    IReadOnlyList<ContextMemorySlice>? MemorySlices = null,
    IReadOnlyList<ContextSkillSlice>? SkillSlices = null,
    ContextPersonaSlice? Persona = null);

public sealed record ContextBundleDocument(
    string DocumentId,
    string Checksum,
    string SelectionReason,
    DocumentLoadPolicy LoadPolicy,
    int EstimatedTokens,
    bool Truncated);

public sealed record ContextBundleSegment(
    ContextSegmentKind Kind,
    string SourceId,
    string Content,
    int EstimatedTokens,
    bool Mandatory,
    string? CitationReference = null);

public sealed record ContextBundle(
    string ManifestVersion,
    IReadOnlyList<ContextBundleDocument> Documents,
    IReadOnlyList<ContextBundleSegment> Segments,
    int EstimatedTokens,
    IReadOnlyList<string> Truncated,
    IReadOnlyList<string> Conflicts,
    int CacheHits,
    string BundleChecksum,
    string RenderedContext);

public sealed class ContextBundleConflictException(IReadOnlyList<string> conflicts)
    : Exception("Canonical context bundle conflicts prevent delivery.")
{
    public IReadOnlyList<string> Conflicts { get; } = conflicts;
}

public sealed class ContextBundleBuilder
{
    private static readonly ConcurrentDictionary<string, ContextBundle> Cache =
        new(StringComparer.Ordinal);

    private readonly string _repositoryRoot;
    private readonly GovernanceManifestService _manifestService;

    public ContextBundleBuilder(string repositoryRoot)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _manifestService = new GovernanceManifestService(_repositoryRoot);
    }

    public ContextBundle Build(ContextBundleRequest request)
    {
        Validate(request);
        var manifest = _manifestService.LoadAndValidate();
        var documents = SelectDocuments(manifest, request);
        var conflicts = DetectConflicts(documents);
        var segments = LoadDocumentSegments(documents, conflicts);
        AddRuntimeSegments(segments, request);
        if (conflicts.Count > 0)
        {
            return BlockedBundle(manifest, documents, segments, conflicts, request);
        }

        var selected = ApplyBudget(segments, request.TokenBudget, out var truncated);
        var rendered = Render(selected);
        if (SecretTextProtector.ContainsSecret(rendered))
        {
            conflicts.Add("bundle_contains_secret");
            return BlockedBundle(manifest, documents, segments, conflicts, request);
        }

        var checksum = Sha256(rendered);
        var cacheKey = $"{request.TenantId}:{request.ProjectId}:{checksum}";
        if (Cache.TryGetValue(cacheKey, out var cached))
        {
            return cached with { CacheHits = cached.CacheHits + 1 };
        }

        var includedIds = selected.Select(segment => segment.SourceId).ToHashSet(StringComparer.Ordinal);
        var result = new ContextBundle(
            manifest.ManifestVersion,
            documents.Select(document => new ContextBundleDocument(
                document.Id,
                document.Checksum,
                SelectionReason(document, request),
                document.LoadPolicy,
                document.TokenEstimate,
                !includedIds.Contains(document.Id))).ToArray(),
            selected,
            selected.Sum(segment => segment.EstimatedTokens),
            truncated,
            [],
            0,
            checksum,
            rendered);
        Cache[cacheKey] = result;
        return result;
    }

    public ContextBundle BuildOrFallback(ContextBundleRequest request)
    {
        try
        {
            return Build(request);
        }
        catch (Exception exception) when (exception is GovernanceManifestException or IOException or UnauthorizedAccessException)
        {
            Validate(request);
            var segments = new List<ContextBundleSegment>();
            AddRuntimeSegments(segments, request);
            var fallback = segments.Where(segment => segment.Mandatory).OrderBy(segment => segment.Kind).ToArray();
            var rendered = Render(fallback);
            return new ContextBundle(
                "0.0.0",
                [],
                fallback,
                fallback.Sum(segment => segment.EstimatedTokens),
                [],
                [$"bundle_build_failed:{exception.GetType().Name}"],
                0,
                Sha256(rendered),
                rendered);
        }
    }

    private static ContextBundle BlockedBundle(
        GovernanceManifest manifest,
        IReadOnlyList<GovernanceDocument> documents,
        IReadOnlyList<ContextBundleSegment> segments,
        IReadOnlyList<string> conflicts,
        ContextBundleRequest request)
    {
        var fallback = segments.Where(segment => segment.Mandatory).OrderBy(segment => segment.Kind).ToArray();
        var rendered = Render(fallback);
        return new ContextBundle(
            manifest.ManifestVersion,
            documents.Select(document => new ContextBundleDocument(
                document.Id,
                document.Checksum,
                SelectionReason(document, request),
                document.LoadPolicy,
                document.TokenEstimate,
                !fallback.Any(segment => segment.SourceId == document.Id))).ToArray(),
            fallback,
            fallback.Sum(segment => segment.EstimatedTokens),
            documents.Select(document => document.Id).Where(id => fallback.All(segment => segment.SourceId != id)).ToArray(),
            conflicts,
            0,
            Sha256(rendered),
            rendered);
    }

    private static void Validate(ContextBundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AttemptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StatusDigestJson);
        ArgumentNullException.ThrowIfNull(request.Paths);
        if (request.TokenBudget < 256)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Context budget must be at least 256 tokens.");
        }
    }

    private static GovernanceDocument[] SelectDocuments(
        GovernanceManifest manifest,
        ContextBundleRequest request) => manifest.Documents
        .Where(document => document.Status == DocumentStatus.Active &&
            document.LoadPolicy is DocumentLoadPolicy.Always or DocumentLoadPolicy.Entry or DocumentLoadPolicy.Bundle &&
            Matches(document.Providers, request.Provider) &&
            Matches(document.Agents, request.AgentId) &&
            Matches(document.Workflows, request.Workflow) &&
            Matches(document.Phases, request.Phase) &&
            Matches(document.TaskTypes, request.TaskType) &&
            Matches(document.RiskTiers, request.RiskTier) &&
            MatchesPaths(document.PathGlobs, request.Paths))
        .OrderBy(document => Order(document))
        .ThenByDescending(document => document.Priority)
        .ThenBy(document => document.Id, StringComparer.Ordinal)
        .ToArray();

    private static List<string> DetectConflicts(IReadOnlyList<GovernanceDocument> documents) =>
        documents
            .Where(document => document.Authority == DocumentAuthority.Canonical)
            .GroupBy(document => $"{document.Topic}:{document.Scope}", StringComparer.Ordinal)
            .Where(group => group.Count() > 1 && !group.Any(candidate =>
                group.Any(other => candidate.Supersedes.Contains(other.Id, StringComparer.Ordinal))))
            .Select(group => $"canonical_conflict:{group.Key}")
            .Order(StringComparer.Ordinal)
            .ToList();

    private List<ContextBundleSegment> LoadDocumentSegments(
        IReadOnlyList<GovernanceDocument> documents,
        List<string> conflicts)
    {
        var segments = new List<ContextBundleSegment>();
        foreach (var document in documents)
        {
            var path = Path.Combine(_repositoryRoot, document.Path.Replace('/', Path.DirectorySeparatorChar));
            var content = File.ReadAllText(path);
            if (!string.Equals($"sha256:{Sha256(content)}", document.Checksum, StringComparison.Ordinal))
            {
                conflicts.Add($"checksum_drift:{document.Id}");
                continue;
            }

            if (SecretTextProtector.ContainsSecret(content))
            {
                conflicts.Add($"document_contains_secret:{document.Id}");
                continue;
            }

            segments.Add(new ContextBundleSegment(
                DocumentKind(document),
                document.Id,
                content,
                document.TokenEstimate,
                document.Id == "governance-core"));
        }

        return segments;
    }

    private static void AddRuntimeSegments(
        List<ContextBundleSegment> segments,
        ContextBundleRequest request)
    {
        Add(segments, ContextSegmentKind.PathConstraint, "runtime:path-constraints", request.Paths, false);
        Add(segments, ContextSegmentKind.StatusDigest, "runtime:status-digest", [request.StatusDigestJson], false);
        Add(segments, ContextSegmentKind.AcceptanceCriteria, "runtime:acceptance-criteria", request.AcceptanceCriteria, true);
        Add(segments, ContextSegmentKind.ToolPermission, "runtime:tool-permissions", request.ToolPermissions, false);
        Add(segments, ContextSegmentKind.Evidence, "runtime:evidence", request.Evidence, false);
        Add(segments, ContextSegmentKind.StopCondition, "runtime:stop-conditions", request.StopConditions, true);
        Add(segments, ContextSegmentKind.Budget, "runtime:budget", [$"tokenBudget={request.TokenBudget}"], true);
        if (request.Persona is { } persona && !string.IsNullOrWhiteSpace(persona.Key))
        {
            var content = RenderPersona(persona);
            if (content.Length > 0)
            {
                // OBRIGATÓRIA: quem executa precisa saber que especialidade está exercendo, e o
                // orçamento de tokens deve cortar qualquer outra coisa antes de cortar isto.
                segments.Add(new ContextBundleSegment(
                    ContextSegmentKind.Persona,
                    $"persona:{persona.Key}",
                    content,
                    GovernanceManifestSynchronizer.EstimateTokens(content),
                    true));
            }
        }

        foreach (var skill in request.SkillSlices ?? [])
        {
            if (string.IsNullOrWhiteSpace(skill.SkillId) ||
                string.IsNullOrWhiteSpace(skill.Instructions) ||
                string.IsNullOrWhiteSpace(skill.CitationReference) ||
                !SkillAppliesTo(skill, request.Paths))
            {
                continue;
            }

            // NÃO é obrigatória: uma skill aprendida é uma ajuda, e o orçamento de tokens deve
            // cortá-la antes de cortar critério de aceite, condição de parada ou orçamento.
            segments.Add(new ContextBundleSegment(
                ContextSegmentKind.Skill,
                $"skill:{skill.SkillId}",
                $"{skill.Title.Trim()}\n{skill.Instructions.Trim()}\n\nCitation: {skill.CitationReference}",
                Math.Max(1, skill.EstimatedTokens),
                false,
                skill.CitationReference));
        }

        foreach (var slice in request.MemorySlices ?? [])
        {
            if (string.IsNullOrWhiteSpace(slice.DocumentId) ||
                string.IsNullOrWhiteSpace(slice.Content) ||
                string.IsNullOrWhiteSpace(slice.CitationReference))
            {
                continue;
            }

            segments.Add(new ContextBundleSegment(
                ContextSegmentKind.Memory,
                $"memory:{slice.DocumentId}",
                $"{slice.Content.Trim()}\n\nCitation: {slice.CitationReference}",
                Math.Max(1, slice.EstimatedTokens),
                false,
                slice.CitationReference));
        }
    }

    /// <summary>
    /// A skill entra quando não declara escopo (vale para o projeto) ou quando algum de seus
    /// caminhos toca os caminhos do card. A comparação é por PREFIXO de segmento — <c>src/api</c>
    /// cobre <c>src/api/users.cs</c>, mas não <c>src/apiary</c>, que só compartilha o texto.
    /// </summary>
    private static bool SkillAppliesTo(ContextSkillSlice skill, IReadOnlyList<string> paths)
    {
        var scopes = skill.PathScopes ?? [];
        if (scopes.Count == 0)
        {
            return true;
        }

        foreach (var scope in scopes)
        {
            var normalizedScope = NormalizePath(scope);
            if (normalizedScope.Length == 0)
            {
                // Escopo vazio dentro de uma lista declarada não é "tudo": é declaração
                // malformada, e alargar o alcance por causa dela seria decidir a favor do risco.
                continue;
            }

            foreach (var path in paths ?? [])
            {
                var normalizedPath = NormalizePath(path);
                if (normalizedPath.Length == 0)
                {
                    continue;
                }

                if (Covers(normalizedScope, normalizedPath) || Covers(normalizedPath, normalizedScope))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Covers(string outer, string inner) =>
        string.Equals(outer, inner, StringComparison.Ordinal) ||
        (inner.StartsWith(outer, StringComparison.Ordinal) && inner[outer.Length] == '/');

    private static string NormalizePath(string? value) =>
        (value ?? string.Empty).Replace('\\', '/').Trim().TrimEnd('/').TrimStart('.', '/');

    /// <summary>
    /// Renderiza a persona em seções curtas e nomeadas. Listas vazias somem: um cabeçalho seguido
    /// de nada ensina ao agente que a seção é decorativa.
    /// </summary>
    private static string RenderPersona(ContextPersonaSlice persona)
    {
        var builder = new StringBuilder();
        builder.Append("Você atua como ").Append(persona.Name.Trim()).Append('.');
        if (!string.IsNullOrWhiteSpace(persona.Mindset))
        {
            builder.Append('\n').Append(persona.Mindset!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(persona.Mission))
        {
            builder.Append("\n\nMissão: ").Append(persona.Mission!.Trim());
        }

        AppendList(builder, "Como você atua", persona.OperatingPrinciples);
        AppendList(builder, "O que você entrega", persona.Deliverables);
        AppendList(builder, "Onde você para", persona.Limitations);
        return builder.ToString();
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string>? values)
    {
        var items = (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (items.Length == 0)
        {
            return;
        }

        builder.Append("\n\n").Append(title).Append(':');
        foreach (var item in items)
        {
            builder.Append("\n- ").Append(item.Trim());
        }
    }

    private static void Add(
        List<ContextBundleSegment> segments,
        ContextSegmentKind kind,
        string source,
        IReadOnlyList<string> values,
        bool mandatory)
    {
        var content = values.Count == 0 ? "(none declared)" : string.Join('\n', values);
        segments.Add(new ContextBundleSegment(
            kind,
            source,
            content,
            GovernanceManifestSynchronizer.EstimateTokens(content),
            mandatory));
    }

    private static ContextBundleSegment[] ApplyBudget(
        IReadOnlyList<ContextBundleSegment> segments,
        int budget,
        out string[] truncated)
    {
        var selected = new List<ContextBundleSegment>();
        var omitted = new List<string>();
        var used = 0;
        foreach (var segment in segments.OrderBy(segment => segment.Kind).ThenBy(segment => segment.SourceId, StringComparer.Ordinal))
        {
            if (segment.Mandatory || used + segment.EstimatedTokens <= budget)
            {
                selected.Add(segment);
                used += segment.EstimatedTokens;
            }
            else
            {
                omitted.Add(segment.SourceId);
            }
        }

        truncated = omitted.ToArray();
        return selected.ToArray();
    }

    private static string Render(IEnumerable<ContextBundleSegment> segments)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            builder.Append("## ").Append(segment.Kind).Append(" — ").AppendLine(segment.SourceId)
                .AppendLine(segment.Content.Trim()).AppendLine();
        }

        return builder.ToString().TrimEnd() + "\n";
    }

    private static string SelectionReason(GovernanceDocument document, ContextBundleRequest request) =>
        $"{document.LoadPolicy};provider={request.Provider};workflow={request.Workflow};phase={request.Phase};risk={request.RiskTier}";

    private static int Order(GovernanceDocument document) => document.Id == "governance-core" ? 0 : document.Category switch
    {
        "organization" => 1,
        "project" => 2,
        "workflow" => 3,
        "persona" => 4,
        "skill" => 5,
        "rule" when document.Topic == "coordination" => 6,
        _ => 7,
    };

    private static ContextSegmentKind DocumentKind(GovernanceDocument document) => document.Id == "governance-core"
        ? ContextSegmentKind.Core
        : document.Category switch
        {
            "organization" => ContextSegmentKind.Organization,
            "project" => ContextSegmentKind.Project,
            "workflow" => ContextSegmentKind.WorkflowPhase,
            "persona" => ContextSegmentKind.Persona,
            "skill" => ContextSegmentKind.Skill,
            "rule" when document.Topic == "coordination" => ContextSegmentKind.PathConstraint,
            _ => ContextSegmentKind.Project,
        };

    private static bool Matches(IReadOnlyList<string> values, string expected) =>
        values.Contains("*", StringComparer.Ordinal) || values.Contains(expected, StringComparer.OrdinalIgnoreCase);

    private static bool MatchesPaths(IReadOnlyList<string> globs, IReadOnlyList<string> paths) =>
        globs.Contains("**", StringComparer.Ordinal) || paths.Count == 0 || paths.Any(path => globs.Any(glob =>
            glob.EndsWith("/**", StringComparison.Ordinal)
                ? path.StartsWith(glob[..^3].TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, glob[..^3].TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                : string.Equals(path, glob, StringComparison.OrdinalIgnoreCase)));

    private static string Sha256(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
