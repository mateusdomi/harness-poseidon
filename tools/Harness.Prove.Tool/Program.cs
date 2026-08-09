using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Harness.Host.WorkBoard;
using Harness.Modules.Workflows.Product;
using Harness.Persistence.Abstractions.Product;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

var options = Args.Parse(args);
if (options.Has("help") || !options.Has("db") || !options.Has("tenant") ||
    !options.Has("project") || !options.Has("repo"))
{
    Console.Error.WriteLine(
        "Usage: prove-e2e --db <harness.db> --tenant <tenantId> --project <projectId> " +
        "--repo <repoPath> [--task <taskId> --attempt <attemptId>] [--materialize-profile true]");
    return options.Has("help") ? 0 : 2;
}

var databasePath = Path.GetFullPath(options.Required("db"));
var tenantId = options.Required("tenant");
var projectId = options.Required("project");
var repositoryRoot = Path.GetFullPath(options.Required("repo"));
var taskId = options.Value("task");
var attemptId = options.Value("attempt");
var materializeProfile = options.Bool("materialize-profile");

await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath);
var profiles = new SqliteProjectEffectiveProfileStore(dispatcher);
var evidenceSets = new SqliteProductEvidenceSetStore(dispatcher);
var chain = new SqliteWorkChainStore(dispatcher);

var profileRecord = await profiles.GetCurrentAsync(tenantId, projectId);
if (profileRecord is null && materializeProfile)
{
    profileRecord = await MaterializeProfileAsync(databasePath, profiles, tenantId, projectId);
}

if (profileRecord is null ||
    ProjectEffectiveProfile.FromJson(profileRecord.ProfileJson) is not { } profile)
{
    Console.Error.WriteLine("prove-e2e: project has no valid EffectiveProfile; proof cannot be canonical.");
    return 3;
}

var harness = ProductE2EHarness.TryLoad(repositoryRoot);
if (harness is null)
{
    Console.Error.WriteLine("prove-e2e: .harness/e2e.json missing or invalid.");
    return 4;
}

var commitSha = await GitHeadAsync(repositoryRoot);
var e2e = await ProductE2ERunner.RunAsync(repositoryRoot, harness, CancellationToken.None);
var decision = ProductE2EGatePolicy.Decide(e2e);
var now = DateTimeOffset.UtcNow;
var evidenceSetId = UlidValue.New(now).ToString();
var evidenceRecord = await evidenceSets.AppendAsync(CreateEvidenceSet(
    tenantId,
    projectId,
    taskId,
    attemptId,
    commitSha,
    profileRecord,
    profile,
    e2e,
    evidenceSetId,
    now));

var workEvidenceLinked = false;
if (!string.IsNullOrWhiteSpace(taskId) && !string.IsNullOrWhiteSpace(attemptId))
{
    var solicitationId = await ReadSolicitationIdAsync(databasePath, tenantId, projectId, taskId);
    if (solicitationId is null)
    {
        Console.Error.WriteLine("prove-e2e: task not found; product_evidence_set persisted but work_evidence not linked.");
        return 5;
    }

    var linked = await chain.AppendAttemptEvidenceAsync(
        new WorkAttemptEvidenceAppendCommand(
            tenantId,
            solicitationId,
            taskId,
            attemptId,
            [
                new WorkEvidenceInput(
                    UlidValue.New(now.AddTicks(1)).ToString(),
                    $"product-evidence-set:{evidenceSetId}"),
                new WorkEvidenceInput(
                    UlidValue.New(now.AddTicks(2)).ToString(),
                    "proof-type:browser-e2e"),
                new WorkEvidenceInput(
                    UlidValue.New(now.AddTicks(3)).ToString(),
                    $"proof-result:{(e2e.Ran && e2e.Passed ? "pass" : "fail")}"),
                new WorkEvidenceInput(
                    UlidValue.New(now.AddTicks(4)).ToString(),
                    $"git-commit:{commitSha}"),
                new WorkEvidenceInput(
                    UlidValue.New(now.AddTicks(5)).ToString(),
                    $"e2e-run:{e2e.RunId ?? evidenceSetId}"),
            ],
            $"prove-tool:e2e:{attemptId}:{evidenceSetId}",
            now),
        CancellationToken.None);
    workEvidenceLinked =
        linked.Status is WorkChainMutationStatus.Applied or WorkChainMutationStatus.IdempotentReplay;
}

var coverage = await AnalyzeCoverageAsync(databasePath, tenantId, projectId, repositoryRoot, commitSha);

Console.WriteLine(JsonSerializer.Serialize(
    new
    {
        projectId,
        repositoryRoot,
        commitSha,
        e2e.Ran,
        e2e.Passed,
        Decision = decision.ToString(),
        e2e.PassedCount,
        e2e.FailedCount,
        e2e.SkippedCount,
        DurationSeconds = e2e.Duration?.TotalSeconds,
        evidenceSetId = evidenceRecord.EvidenceSetId,
        taskId,
        attemptId,
        workEvidenceLinked,
        requirementAudit = coverage.Audit,
        requirementCoverage = coverage,
        Detail = e2e.Detail,
    },
    Json));

return decision == ProductE2EGateDecision.Passed && workEvidenceLinked && coverage.Ready ? 0 : 10;

static ProductEvidenceSetRecord CreateEvidenceSet(
    string tenantId,
    string projectId,
    string? taskId,
    string? attemptId,
    string commitSha,
    ProjectEffectiveProfileRecord profileRecord,
    ProjectEffectiveProfile profile,
    ProductE2EResult result,
    string evidenceSetId,
    DateTimeOffset now)
{
    var plan = new ProductVerificationPlan(
        profile.Modality,
        profile.BaselineVersion,
        [new ProductVerificationStep(
            ProductEvidenceKind.E2EJourneyPassed,
            Required: true,
            "Prova real de navegador executada pela plataforma contra ambiente efêmero declarado em .harness/e2e.json.",
            ProductEvidenceProvenance.Verified,
            VerificationTrustLevel.PoseidonControlled,
            "ProductE2ERunner",
            ProductVerificationDisposition.RequiredNative)]);
    var item = new ProductEvidence(
        ProductEvidenceKind.E2EJourneyPassed,
        result.Ran && result.Passed,
        result.Detail,
        ProductEvidenceProvenanceRecord.FromVerifier(
            "product-e2e-runner",
            commitSha,
            now,
            "ProductE2ERunner",
            result.Ran && result.Passed ? 0 : 1,
            result.RunId,
            ProductE2EHarness.ManifestPath,
            VerificationTrustLevel.PoseidonControlled));
    var findings = result.Ran && result.Passed
        ? Array.Empty<ProductEvidenceFinding>()
        :
        [
            new ProductEvidenceFinding(
                ProductEvidenceKind.E2EJourneyPassed,
                result.Ran ? ProductEvidenceGap.Failed : ProductEvidenceGap.Missing,
                result.Detail.Length > 1_000 ? result.Detail[..1_000] : result.Detail)
        ];

    return new ProductEvidenceSetRecord(
        tenantId,
        evidenceSetId,
        projectId,
        null,
        taskId,
        attemptId,
        commitSha,
        profileRecord.Version,
        profileRecord.Fingerprint,
        profileRecord.Modality,
        result.Ran && result.Passed ? "satisfied" : "failed",
        JsonSerializer.Serialize(plan.Steps, Json),
        JsonSerializer.Serialize(new[] { item }, Json),
        JsonSerializer.Serialize(findings, Json),
        "product-e2e-runner",
        now);
}

static async Task<ProjectEffectiveProfileRecord?> MaterializeProfileAsync(
    string databasePath,
    IProjectEffectiveProfileStore profiles,
    string tenantId,
    string projectId)
{
    var text = await ReadProjectProofContextAsync(databasePath, tenantId, projectId);
    var directives = ProfileDirectiveParser.Parse(
        text,
        ProfileAuthority.ProjectRequirement,
        projectId,
        "work-objective");
    var profile = EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs(projectId, text, directives));
    var saved = await profiles.SaveAsync(
        new ProjectEffectiveProfileSaveCommand(
            tenantId,
            projectId,
            profile.Fingerprint(),
            profile.BaselineVersion,
            profile.Modality.ToString(),
            profile.ToJson(),
            DateTimeOffset.UtcNow,
            "harness-prove-tool:e2e"));
    return saved.Profile;
}

static async Task<string> ReadProjectProofContextAsync(
    string databasePath,
    string tenantId,
    string projectId)
{
    var parts = new List<string>();
    await using var connection = await OpenReadAsync(databasePath);
    await using (var project = connection.CreateCommand())
    {
        project.CommandText =
            "SELECT name, description, technologies_json FROM projects WHERE tenant_id=$tenant AND id=$project;";
        project.Parameters.AddWithValue("$tenant", tenantId);
        project.Parameters.AddWithValue("$project", projectId);
        await using var reader = await project.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            parts.Add(reader.GetString(0));
            parts.Add(reader.GetString(1));
            parts.Add(reader.GetString(2));
        }
    }

    await using (var work = connection.CreateCommand())
    {
        work.CommandText =
            """
            SELECT title, description, acceptance_criteria_json
            FROM demands
            WHERE tenant_id=$tenant AND project_id=$project
            UNION ALL
            SELECT title, '', ''
            FROM work_tasks
            WHERE tenant_id=$tenant AND project_id=$project
            LIMIT 80;
            """;
        work.Parameters.AddWithValue("$tenant", tenantId);
        work.Parameters.AddWithValue("$project", projectId);
        await using var reader = await work.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            parts.Add(reader.GetString(0));
            parts.Add(reader.GetString(1));
            parts.Add(reader.GetString(2));
        }
    }

    return string.Join("\n\n", parts);
}

static async Task<string?> ReadSolicitationIdAsync(
    string databasePath,
    string tenantId,
    string projectId,
    string taskId)
{
    await using var connection = await OpenReadAsync(databasePath);
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        SELECT d.solicitation_id
        FROM work_tasks t
        JOIN demands d ON d.id=t.demand_id AND d.tenant_id=t.tenant_id AND d.project_id=t.project_id
        WHERE t.tenant_id=$tenant AND t.project_id=$project AND t.id=$task;
        """;
    command.Parameters.AddWithValue("$tenant", tenantId);
    command.Parameters.AddWithValue("$project", projectId);
    command.Parameters.AddWithValue("$task", taskId);
    var value = await command.ExecuteScalarAsync();
    return value as string;
}

static async Task<CoverageReport> AnalyzeCoverageAsync(
    string databasePath,
    string tenantId,
    string projectId,
    string repositoryRoot,
    string commitSha)
{
    var demands = new List<DemandRow>();
    var tasksByDemand = new Dictionary<string, List<RequirementCard>>(StringComparer.Ordinal);
    var proofs = new List<RequirementProof>();

    await using var connection = await OpenReadAsync(databasePath);
    await using (var command = connection.CreateCommand())
    {
        command.CommandText =
            """
            SELECT id, title, state, acceptance_criteria_json
            FROM demands
            WHERE tenant_id=$tenant AND project_id=$project AND state <> 'cancelled'
            ORDER BY created_at, id;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$project", projectId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            demands.Add(new DemandRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                AcceptanceCriteria(reader.GetString(3))));
        }
    }

    await using (var command = connection.CreateCommand())
    {
        command.CommandText =
            """
            SELECT demand_id, id, title, state, archived_at IS NOT NULL
            FROM work_tasks
            WHERE tenant_id=$tenant AND project_id=$project
            ORDER BY created_at, id;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$project", projectId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var demandId = reader.GetString(0);
            if (!tasksByDemand.TryGetValue(demandId, out var tasks))
            {
                tasks = [];
                tasksByDemand[demandId] = tasks;
            }

            tasks.Add(new RequirementCard(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4)));
        }
    }

    await using (var command = connection.CreateCommand())
    {
        command.CommandText =
            """
            SELECT p.evidence_set_id, p.task_id, p.commit_sha, t.demand_id, d.acceptance_criteria_json
            FROM product_evidence_sets p
            JOIN work_tasks t
              ON t.tenant_id=p.tenant_id AND t.project_id=p.project_id AND t.id=p.task_id
            JOIN demands d
              ON d.tenant_id=t.tenant_id AND d.project_id=t.project_id AND d.id=t.demand_id
            WHERE p.tenant_id=$tenant
              AND p.project_id=$project
              AND p.commit_sha=$commit
              AND p.gate_decision='satisfied'
              AND p.task_id IS NOT NULL
            ORDER BY p.created_at DESC;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$commit", commitSha);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var evidenceSetId = reader.GetString(0);
            var objectiveCardId = reader.GetString(1);
            var proofCommit = reader.GetString(2);
            var demandId = reader.GetString(3);
            var criteria = AcceptanceCriteria(reader.GetString(4));
            for (var index = 0; index < criteria.Count; index++)
            {
                var criterionId = CriterionId(demandId, index);
                proofs.Add(new RequirementProof(
                    criterionId,
                    objectiveCardId,
                    evidenceSetId,
                    proofCommit,
                    true,
                    [criterionId]));
            }
        }
    }

    var stalePassingEvidenceSetsIgnored = await CountStalePassingEvidenceSetsAsync(
        connection,
        tenantId,
        projectId,
        commitSha);
    var objectives = demands.Select(demand =>
    {
        var cards = tasksByDemand.TryGetValue(demand.Id, out var demandCards) ? demandCards : [];
        var text = string.Join(
            "\n",
            demand.Title,
            string.Join("\n", demand.AcceptanceCriteria));
        return new ObjectiveBinding(
            demand.Id,
            cards.FirstOrDefault()?.Id ?? demand.Id,
            demand.Title,
            text,
            cards);
    }).ToArray();
    var canonical = ReadCanonicalRequirements(repositoryRoot, objectives).ToArray();
    var usingCanonicalCatalog = canonical.Length > 0;
    var machineRequirements = canonical
        .Where(item => item.IsMachineApplicable)
        .ToArray();

    var requirements = new List<(string Id, string Title, bool Superseded)>();
    var cardsByRequirement = new Dictionary<string, IReadOnlyList<RequirementCard>>(StringComparer.Ordinal);
    if (usingCanonicalCatalog)
    {
        foreach (var criterion in machineRequirements)
        {
            var title = $"{criterion.SourceRef} :: {criterion.Description}";
            requirements.Add((
                criterion.CriterionId,
                title,
                false));
            var cards = new List<RequirementCard>();
            if (criterion.ObjectiveTaskId is { Length: > 0 } taskId)
            {
                cards.Add(new RequirementCard(taskId, criterion.ObjectiveTitle ?? taskId, "completed", false));
            }

            // The final product browser proof is a real objective/card in these projects. It may
            // cover only criteria whose canonical matrix names a browser/runtime proof; deterministic
            // and human criteria remain uncovered unless they have their own compatible evidence.
            if (criterion.ExpectedProofType == "browser-e2e")
            {
                foreach (var task in objectives
                    .Where(item => IsFinalBrowserProofObjective(item.Title))
                    .Select(item => new RequirementCard(item.TaskId, item.Title, "completed", false)))
                {
                    if (cards.All(existing => existing.Id != task.Id))
                    {
                        cards.Add(task);
                    }
                }
            }

            cardsByRequirement[criterion.CriterionId] = cards;
        }
    }
    else
    {
        foreach (var demand in demands)
        {
            var criteria = demand.AcceptanceCriteria.Count > 0 ? demand.AcceptanceCriteria : [demand.Title];
            for (var index = 0; index < criteria.Count; index++)
            {
                var criterionId = CriterionId(demand.Id, index);
                requirements.Add((
                    criterionId,
                    $"{demand.Title} :: {criteria[index]}",
                    string.Equals(demand.State, "superseded", StringComparison.OrdinalIgnoreCase)));
                cardsByRequirement[criterionId] =
                    tasksByDemand.TryGetValue(demand.Id, out var cards) ? cards : [];
            }
        }
    }

    if (usingCanonicalCatalog)
    {
        var canonicalProofs = await ReadCanonicalProofsAsync(
            connection,
            tenantId,
            projectId,
            commitSha,
            machineRequirements);
        proofs.AddRange(canonicalProofs);
        var evidenceByCriterion = canonicalProofs
            .GroupBy(proof => proof.RequirementId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => string.Join(
                    ",",
                    group.Select(proof => proof.EvidenceSetId).Distinct(StringComparer.Ordinal)),
                StringComparer.Ordinal);
        canonical = [.. canonical.Select(criterion => criterion with
        {
            CurrentEvidence = evidenceByCriterion.GetValueOrDefault(criterion.CriterionId)
        })];
        machineRequirements = [.. canonical.Where(item => item.IsMachineApplicable)];
    }

    var coverage = RequirementCoverageAnalyzer.Analyze(
        requirements,
        cardsByRequirement,
        proofs,
        commitSha);
    var readiness = HumanAcceptanceReadinessGate.Evaluate(coverage);
    var covered = coverage.Count(item => item.IsCovered);
    var missing = coverage.Where(item => !item.IsCovered).ToArray();
    var percent = coverage.Count == 0 ? 100 : covered * 100.0 / coverage.Count;
    return new CoverageReport(
        coverage.Count,
        covered,
        missing.Length,
        Math.Round(percent, 2),
        missing.Length == 0 && proofs.All(proof =>
            string.Equals(proof.CommitSha, commitSha, StringComparison.OrdinalIgnoreCase)),
        readiness.Ready,
        stalePassingEvidenceSetsIgnored,
        new RequirementAuditReport(
            usingCanonicalCatalog,
            canonical.Length,
            machineRequirements.Length,
            canonical.Count(item => item.ExpectedProofType == "human-acceptance"),
            canonical.Count(item => !item.IsMachineApplicable && item.ExpectedProofType != "human-acceptance"),
            0,
            canonical),
        [.. missing.Take(20).Select(item => new MissingCriterion(
            item.RequirementId,
            item.Title,
            item.Status.ToString(),
            item.Reason))]);
}

static async Task<IReadOnlyList<RequirementProof>> ReadCanonicalProofsAsync(
    SqliteConnection connection,
    string tenantId,
    string projectId,
    string commitSha,
    IReadOnlyList<CanonicalCriterion> criteria)
{
    var proofs = new List<RequirementProof>();
    var browserCriteria = criteria
        .Where(item => item.ExpectedProofType == "browser-e2e")
        .ToArray();
    if (browserCriteria.Length == 0)
    {
        return proofs;
    }

    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        SELECT evidence_set_id, task_id, commit_sha
        FROM product_evidence_sets
        WHERE tenant_id=$tenant
          AND project_id=$project
          AND commit_sha=$commit
          AND gate_decision='satisfied'
          AND task_id IS NOT NULL
          AND collectors LIKE '%product-e2e-runner%'
        ORDER BY created_at DESC;
        """;
    command.Parameters.AddWithValue("$tenant", tenantId);
    command.Parameters.AddWithValue("$project", projectId);
    command.Parameters.AddWithValue("$commit", commitSha);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var evidenceSetId = reader.GetString(0);
        var objectiveCardId = reader.GetString(1);
        var proofCommit = reader.GetString(2);
        foreach (var criterion in browserCriteria)
        {
            proofs.Add(new RequirementProof(
                criterion.CriterionId,
                objectiveCardId,
                evidenceSetId,
                proofCommit,
                true,
                [criterion.CriterionId]));
        }
    }

    return proofs;
}

static IReadOnlyList<CanonicalCriterion> ReadCanonicalRequirements(
    string repositoryRoot,
    IReadOnlyList<ObjectiveBinding> objectives)
{
    var matrixPath = Path.Combine(repositoryRoot, "docs", "RASTREABILIDADE.md");
    var prismaSourcePath = Path.Combine(repositoryRoot, "docs", "REQUISITOS-FONTE.md");
    var indicadoresSourcePath = Path.Combine(repositoryRoot, "docs", "REQUISITOS-FONTE.txt");
    if (!File.Exists(matrixPath))
    {
        return [];
    }

    var matrix = File.ReadAllText(matrixPath);
    if (File.Exists(prismaSourcePath) &&
        matrix.Contains("T1", StringComparison.Ordinal) &&
        matrix.Contains("T26", StringComparison.Ordinal))
    {
        return ReadPrismaCanonical(prismaSourcePath, matrix, objectives);
    }

    if (File.Exists(indicadoresSourcePath) &&
        matrix.Contains("São 22 itens", StringComparison.OrdinalIgnoreCase))
    {
        return ReadIndicadoresCanonical(indicadoresSourcePath, matrix, objectives);
    }

    return [];
}

static IReadOnlyList<CanonicalCriterion> ReadPrismaCanonical(
    string sourcePath,
    string matrix,
    IReadOnlyList<ObjectiveBinding> objectives)
{
    var source = File.ReadAllText(sourcePath);
    var criteria = new List<CanonicalCriterion>();
    foreach (Match match in Regex.Matches(
        source,
        @"-\s+\*\*(T\d{1,2})\*\*\s+(?<description>.+)",
        RegexOptions.CultureInvariant))
    {
        var id = match.Groups[1].Value;
        var description = match.Groups["description"].Value.Trim();
        var matrixLine = MatrixLine(matrix, id);
        var proofType = PrismaProofTypeFor(matrixLine);
        var objective = MatchObjective(id, description, objectives);
        criteria.Add(new CanonicalCriterion(
            id,
            "docs/REQUISITOS-FONTE.md §16",
            Shorten(description),
            "Required",
            objective?.DemandId,
            objective?.TaskId,
            objective?.Title,
            proofType,
            null,
            proofType != "human-acceptance"));
    }

    return [.. criteria.OrderBy(item => int.Parse(item.CriterionId[1..], System.Globalization.CultureInfo.InvariantCulture))];
}

static IReadOnlyList<CanonicalCriterion> ReadIndicadoresCanonical(
    string sourcePath,
    string matrix,
    IReadOnlyList<ObjectiveBinding> objectives)
{
    var source = File.ReadAllText(sourcePath);
    var sourceItems = ExtractIndicadoresSourceItems(source);
    var criteria = new List<CanonicalCriterion>();
    var previousProofType = "deterministic";
    foreach (Match match in Regex.Matches(
        matrix,
        @"(?ms)^###\s+(?<number>\d{1,2})\.\s+(?<title>[^\n]+)\n(?<body>.*?)(?=^###\s+\d{1,2}\.\s+|\n---\n|\\z)",
        RegexOptions.CultureInvariant))
    {
        var number = int.Parse(match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var id = $"AC{number:00}";
        var title = match.Groups["title"].Value.Trim();
        var body = match.Groups["body"].Value;
        var proofType = ProofTypeFor(body);
        if (body.Contains("mesma spec", StringComparison.OrdinalIgnoreCase))
        {
            proofType = previousProofType;
        }

        previousProofType = proofType;
        var objective = PreferredIndicadoresObjective(number, objectives) ?? MatchObjective(id, title, objectives);
        var sourceDescription = sourceItems.GetValueOrDefault(number, title);
        criteria.Add(new CanonicalCriterion(
            id,
            "docs/REQUISITOS-FONTE.txt §32",
            Shorten(sourceDescription),
            "Required",
            objective?.DemandId,
            objective?.TaskId,
            objective?.Title,
            proofType,
            null,
            proofType != "human-acceptance"));
    }

    return [.. criteria.OrderBy(item => item.CriterionId, StringComparer.Ordinal)];
}

static ObjectiveBinding? PreferredIndicadoresObjective(
    int criterionNumber,
    IReadOnlyList<ObjectiveBinding> objectives)
{
    var fragment = criterionNumber switch
    {
        >= 1 and <= 3 => "Fatia vertical",
        4 or 14 => "Usuários, perfis e permissões",
        5 or 6 or 22 => "Organização e cadastro",
        7 or 8 => "Modelos de importação",
        >= 9 and <= 13 => "Importação completa",
        15 or 19 or 20 => "Dashboard configurável",
        16 or 21 => "Rastreabilidade total",
        17 or 18 => "produto passa na suíte E2E",
        _ => string.Empty,
    };
    return fragment.Length == 0
        ? null
        : objectives.FirstOrDefault(item => item.Title.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}

static Dictionary<int, string> ExtractIndicadoresSourceItems(string source)
{
    var start = source.IndexOf("32. CRITÉRIOS DE ACEITE", StringComparison.OrdinalIgnoreCase);
    if (start < 0)
    {
        return [];
    }

    var end = source.IndexOf("33.", start, StringComparison.OrdinalIgnoreCase);
    var section = end > start ? source[start..end] : source[start..];
    var lines = section
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => line.EndsWith(';') || line.EndsWith('.'))
        .Where(line => !line.StartsWith("O projeto", StringComparison.OrdinalIgnoreCase))
        .Select(line => line.TrimEnd(';', '.').Trim())
        .Where(line => line.Length > 0)
        .ToArray();
    var result = new Dictionary<int, string>();
    for (var index = 0; index < lines.Length; index++)
    {
        result[index + 1] = lines[index];
    }

    return result;
}

static string MatrixLine(string matrix, string criterionId)
{
    return matrix
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault(line => line.Contains($"**{criterionId}**", StringComparison.Ordinal)) ??
        string.Empty;
}

static string PrismaProofTypeFor(string matrixLine)
{
    if (matrixLine.Contains("frontend/e2e", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(matrixLine, @"\|\s*idem\s*\|?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
    {
        return "browser-e2e";
    }

    if (matrixLine.Contains("Oracle", StringComparison.OrdinalIgnoreCase) ||
        matrixLine.Contains("gatilho", StringComparison.OrdinalIgnoreCase) ||
        matrixLine.Contains("banco", StringComparison.OrdinalIgnoreCase))
    {
        return "database";
    }

    return "deterministic";
}

static string ProofTypeFor(string text)
{
    if (text.Contains("passo manual justificado", StringComparison.OrdinalIgnoreCase) &&
        !text.Contains("navegador", StringComparison.OrdinalIgnoreCase) &&
        !text.Contains("Playwright", StringComparison.OrdinalIgnoreCase) &&
        !text.Contains("E2E", StringComparison.OrdinalIgnoreCase))
    {
        return "human-acceptance";
    }

    if (text.Contains("navegador", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Playwright", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("e2e", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("frontend/e2e", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("tests/e2e", StringComparison.OrdinalIgnoreCase))
    {
        return "browser-e2e";
    }

    if (text.Contains("Oracle", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("banco", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("migration", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("gatilho", StringComparison.OrdinalIgnoreCase))
    {
        return "database";
    }

    return "deterministic";
}

static ObjectiveBinding? MatchObjective(
    string criterionId,
    string description,
    IReadOnlyList<ObjectiveBinding> objectives)
{
    var best = objectives
        .Select(item => new { Objective = item, Score = ObjectiveScore(criterionId, description, item.SearchText) })
        .Where(item => item.Score > 0)
        .OrderByDescending(item => item.Score)
        .FirstOrDefault();
    return best?.Objective;
}

static int ObjectiveScore(string criterionId, string description, string text)
{
    var score = 0;
    if (criterionId.StartsWith('T') && MentionsPrismaCriterion(text, criterionId))
    {
        score += 100;
    }

    foreach (var token in Tokens(description).Take(14))
    {
        if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            score++;
        }
    }

    if (IsFinalBrowserProofObjective(text))
    {
        score -= 50;
    }

    return score;
}

static bool MentionsPrismaCriterion(string text, string criterionId)
{
    if (Regex.IsMatch(text, $@"(?<![A-Z0-9]){Regex.Escape(criterionId)}(?![0-9])"))
    {
        return true;
    }

    var number = int.Parse(criterionId[1..], System.Globalization.CultureInfo.InvariantCulture);
    foreach (Match range in Regex.Matches(
        text,
        @"T(?<start>\d{1,2})\s*(?:-|–|—|a|até)\s*T?(?<end>\d{1,2})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
    {
        var start = int.Parse(range.Groups["start"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var end = int.Parse(range.Groups["end"].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (number >= Math.Min(start, end) && number <= Math.Max(start, end))
        {
            return true;
        }
    }

    return false;
}

static bool IsFinalBrowserProofObjective(string text) =>
    text.Contains("E2E de navegador", StringComparison.OrdinalIgnoreCase) ||
    text.Contains("suíte E2E de navegador", StringComparison.OrdinalIgnoreCase) ||
    text.Contains("produto passa na suíte E2E", StringComparison.OrdinalIgnoreCase) ||
    text.Contains("Rastreabilidade total", StringComparison.OrdinalIgnoreCase);

static IEnumerable<string> Tokens(string value)
{
    var stop = new HashSet<string>([
        "para", "com", "uma", "um", "dos", "das", "que", "por", "meio", "sistema",
        "funcionar", "funcionarem", "conseguir", "conectar", "ficar", "ficarem",
        "estar", "estiverem", "critério", "aceite", "de", "do", "da", "os", "as",
        "ao", "no", "na", "o", "a", "e"
    ], StringComparer.OrdinalIgnoreCase);
    foreach (Match match in Regex.Matches(value, @"[\p{L}\p{Nd}]{4,}", RegexOptions.CultureInvariant))
    {
        var token = match.Value;
        if (!stop.Contains(token))
        {
            yield return token;
        }
    }
}

static string Shorten(string value)
{
    value = Regex.Replace(value.Trim(), @"\s+", " ");
    return value.Length <= 180 ? value : value[..177] + "...";
}

static async Task<int> CountStalePassingEvidenceSetsAsync(
    SqliteConnection connection,
    string tenantId,
    string projectId,
    string commitSha)
{
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        SELECT COUNT(*)
        FROM product_evidence_sets
        WHERE tenant_id=$tenant
          AND project_id=$project
          AND gate_decision='satisfied'
          AND commit_sha <> $commit;
        """;
    command.Parameters.AddWithValue("$tenant", tenantId);
    command.Parameters.AddWithValue("$project", projectId);
    command.Parameters.AddWithValue("$commit", commitSha);
    var value = await command.ExecuteScalarAsync();
    return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
}

static IReadOnlyList<string> AcceptanceCriteria(string json)
{
    try
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        return [.. document.RootElement.EnumerateArray()
            .Select(item => item.ValueKind is JsonValueKind.String
                ? item.GetString()
                : item.GetRawText())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)];
    }
    catch (JsonException)
    {
        return [];
    }
}

static string CriterionId(string demandId, int index) =>
    string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{demandId}#ac{index + 1}");

static async Task<SqliteConnection> OpenReadAsync(string databasePath)
{
    var connection = new SqliteConnection(
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
    await connection.OpenAsync();
    return connection;
}

static async Task<string> GitHeadAsync(string repositoryRoot)
{
    var start = new ProcessStartInfo
    {
        FileName = "git",
        WorkingDirectory = repositoryRoot,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    start.ArgumentList.Add("rev-parse");
    start.ArgumentList.Add("HEAD");
    using var process = Process.Start(start) ??
        throw new InvalidOperationException("Could not start git.");
    var output = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException("git rev-parse HEAD failed.");
    }

    return output.Trim();
}

internal sealed class Args
{
    private readonly Dictionary<string, string> _values;

    private Args(Dictionary<string, string> values) => _values = values;

    public static Args Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            if (!current.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = current[2..];
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "true";
            values[key] = value;
        }

        return new Args(values);
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public string Required(string key) => _values.TryGetValue(key, out var value) && value.Length > 0
        ? value
        : throw new ArgumentException($"Missing --{key}");

    public string? Value(string key) => _values.GetValueOrDefault(key);

    public bool Bool(string key) =>
        _values.TryGetValue(key, out var value) &&
        bool.TryParse(value, out var parsed) &&
        parsed;
}

internal sealed record DemandRow(
    string Id,
    string Title,
    string State,
    IReadOnlyList<string> AcceptanceCriteria);

internal sealed record ObjectiveBinding(
    string DemandId,
    string TaskId,
    string Title,
    string SearchText,
    IReadOnlyList<RequirementCard> Cards);

internal sealed record CanonicalCriterion(
    string CriterionId,
    string SourceRef,
    string Description,
    string Binding,
    string? ObjectiveId,
    string? ObjectiveTaskId,
    string? ObjectiveTitle,
    string ExpectedProofType,
    string? CurrentEvidence,
    bool IsMachineApplicable);

internal sealed record RequirementAuditReport(
    bool UsedCanonicalCatalog,
    int Canonical,
    int RequiredApplicable,
    int HumanAcceptance,
    int NonApplicable,
    int Lost,
    IReadOnlyList<CanonicalCriterion> Criteria);

internal sealed record MissingCriterion(
    string Id,
    string Title,
    string Status,
    string Reason);

internal sealed record CoverageReport(
    int RequiredCriteriaTotal,
    int RequiredCriteriaCovered,
    int RequiredCriteriaMissing,
    double CoveragePercent,
    bool EvidenceValidForCurrentCommit,
    bool Ready,
    int StalePassingEvidenceSetsIgnored,
    RequirementAuditReport Audit,
    IReadOnlyList<MissingCriterion> MissingCriteriaSample);

internal static partial class Program
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };
}
