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
    ContextPersonaSlice? Persona = null,

    /// <summary>
    /// Papel LÓGICO de quem executa (<c>backend-specialist</c>, <c>critic</c>, …). Dimensão
    /// própria, distinta de <see cref="TaskType"/>: o papel responde "quem executa", o tipo de
    /// card responde "que trabalho é este". Usar um como substituto do outro foi o defeito que
    /// fazia o manifesto selecionar por um vocabulário que o runtime nunca enviava.
    /// </summary>
    string AgentRole = "",

    /// <summary>
    /// Perfil efetivo do projeto, na forma compacta. É contexto OBRIGATÓRIO onde existe: sem ele o
    /// executor decide stack, arquitetura e modalidade por conta própria — que é exatamente o
    /// defeito que originou este trabalho.
    /// </summary>
    string? ProductProfileSummary = null);

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

/// <summary>
/// Um item que ficou de fora do bundle, com o motivo. Existia só a lista de ids: para investigar
/// depois "a regra existia, foi selecionada, mas não chegou", saber QUE item caiu não basta —
/// é preciso saber POR QUE e sob qual política de carga ele estava.
/// </summary>
public sealed record ContextBundleTruncation(
    string SourceId,
    string Reason,
    string LoadPolicy,
    int EstimatedTokens);

public sealed record ContextBundle(
    string ManifestVersion,
    IReadOnlyList<ContextBundleDocument> Documents,
    IReadOnlyList<ContextBundleSegment> Segments,
    int EstimatedTokens,
    IReadOnlyList<string> Truncated,
    IReadOnlyList<string> Conflicts,
    int CacheHits,
    string BundleChecksum,
    string RenderedContext,
    IReadOnlyList<ContextBundleTruncation>? TruncationDetails = null)
{
    /// <summary>Motivo por item truncado. Nunca nulo; vazio quando nada foi cortado.</summary>
    public IReadOnlyList<ContextBundleTruncation> Truncations { get; } = TruncationDetails ?? [];
}

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

        var selected = ApplyBudget(
            segments, request.TokenBudget, out var truncated, out var mandatoryOverflow);
        if (mandatoryOverflow is not null)
        {
            // Fail-closed: contexto obrigatório que não cabe bloqueia a execução com diagnóstico,
            // em vez de entregar um agente sem as regras que o governam.
            conflicts.Add(mandatoryOverflow);
            return BlockedBundle(manifest, documents, segments, conflicts, request);
        }

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
            truncated.Select(item => item.SourceId).ToArray(),
            [],
            0,
            checksum,
            rendered,
            truncated);
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
            MatchesAny(document.Agents, ContextSelectorVocabulary.AgentAliases(request.AgentId, request.Persona?.Key)) &&
            MatchesAny(document.Roles, ContextSelectorVocabulary.RoleAliases(request.AgentRole)) &&
            MatchesAny(document.Workflows, ContextSelectorVocabulary.WorkflowAliases(request.Workflow)) &&
            MatchesAny(document.Phases, ContextSelectorVocabulary.PhaseAliases(request.Phase)) &&
            MatchesAny(document.TaskTypes, ContextSelectorVocabulary.TaskTypeAliases(request.TaskType)) &&
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
                // O manifesto já declara o que é indispensável: `always`/`entry` significa "entra
                // em todo bundle". Antes só o núcleo era obrigatório e qualquer documento
                // `always` podia ser cortado pelo orçamento — a política dizia uma coisa e o
                // corte fazia outra.
                document.LoadPolicy is DocumentLoadPolicy.Always or DocumentLoadPolicy.Entry));
        }

        return segments;
    }

    private static void AddRuntimeSegments(
        List<ContextBundleSegment> segments,
        ContextBundleRequest request)
    {
        // OBRIGATÓRIO: o orçamento corta qualquer outra coisa antes de cortar a decisão técnica
        // que governa o projeto.
        if (request.ProductProfileSummary is { Length: > 0 } profile)
        {
            Add(segments, ContextSegmentKind.Project, "runtime:effective-profile", [profile], true);
        }

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

    /// <summary>
    /// Orçamento em duas faixas. O obrigatório entra INTEIRO e primeiro: núcleo de governança,
    /// documentos que o manifesto declara <c>always</c>/<c>entry</c>, critério de aceite, condição
    /// de parada, orçamento e persona. Só o que sobra do orçamento é disputado pelo resto, na
    /// ordem determinística de sempre.
    ///
    /// A regra que faltava: quando o obrigatório sozinho não cabe, o bundle NÃO segue mutilado.
    /// O excesso é devolvido em <paramref name="mandatoryOverflow"/> e o chamador transforma isso
    /// num bundle bloqueado com diagnóstico — porque uma execução que perdeu a regra de segurança
    /// por falta de token não é uma execução governada, é uma execução sem governança que ninguém
    /// percebeu.
    /// </summary>
    private static ContextBundleSegment[] ApplyBudget(
        IReadOnlyList<ContextBundleSegment> segments,
        int budget,
        out ContextBundleTruncation[] truncated,
        out string? mandatoryOverflow)
    {
        var ordered = segments
            .OrderBy(segment => segment.Kind)
            .ThenBy(segment => segment.SourceId, StringComparer.Ordinal)
            .ToArray();

        var selected = new List<ContextBundleSegment>(ordered.Length);
        var omitted = new List<ContextBundleTruncation>();
        var used = 0;

        foreach (var segment in ordered.Where(segment => segment.Mandatory))
        {
            selected.Add(segment);
            used += segment.EstimatedTokens;
        }

        mandatoryOverflow = used > budget
            ? $"mandatory_context_exceeds_budget:{used}/{budget}"
            : null;

        foreach (var segment in ordered.Where(segment => !segment.Mandatory))
        {
            if (used + segment.EstimatedTokens <= budget)
            {
                selected.Add(segment);
                used += segment.EstimatedTokens;
            }
            else
            {
                omitted.Add(new ContextBundleTruncation(
                    segment.SourceId, "budget_exhausted", "bundle", segment.EstimatedTokens));
            }
        }

        truncated = omitted.ToArray();
        return selected
            .OrderBy(segment => segment.Kind)
            .ThenBy(segment => segment.SourceId, StringComparer.Ordinal)
            .ToArray();
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
        $"{document.LoadPolicy};provider={request.Provider};workflow={request.Workflow};" +
        $"phase={request.Phase};cardType={request.TaskType};role={request.AgentRole};" +
        $"risk={request.RiskTier}";

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

    /// <summary>
    /// Casa quando o documento declara <c>*</c> ou qualquer um dos valores aceitáveis daquela
    /// dimensão. Uma lista vazia é curinga: uma dimensão que o manifesto não declara não pode
    /// excluir o documento — foi assim que <c>roles</c> nasceu sem invalidar as 50 entradas
    /// existentes.
    /// </summary>
    private static bool MatchesAny(List<string> values, IReadOnlyList<string> candidates) =>
        values.Count == 0 ||
        values.Contains("*", StringComparer.Ordinal) ||
        candidates.Any(candidate => values.Contains(candidate, StringComparer.OrdinalIgnoreCase));

    private static bool MatchesPaths(IReadOnlyList<string> globs, IReadOnlyList<string> paths) =>
        globs.Contains("**", StringComparer.Ordinal) || paths.Count == 0 || paths.Any(path => globs.Any(glob =>
            glob.EndsWith("/**", StringComparison.Ordinal)
                ? path.StartsWith(glob[..^3].TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, glob[..^3].TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                : string.Equals(path, glob, StringComparison.OrdinalIgnoreCase)));

    private static string Sha256(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
