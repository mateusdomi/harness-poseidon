using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
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

var coverage = await AnalyzeCoverageAsync(databasePath, tenantId, projectId, commitSha);

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
    var requirements = new List<(string Id, string Title, bool Superseded)>();
    var cardsByRequirement = new Dictionary<string, IReadOnlyList<RequirementCard>>(StringComparer.Ordinal);
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
        [.. missing.Take(20).Select(item => new MissingCriterion(
            item.RequirementId,
            item.Title,
            item.Status.ToString(),
            item.Reason))]);
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
