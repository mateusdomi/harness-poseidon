using System.Text.Json;

namespace Harness.Host.V3;

public static class V3ValidationEvidenceGate
{
    public const string ContractVersion = V3MissionCompiler.ValidationMissionContractVersion;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Statuses = ["PASS", "FIXED", "N_A", "FAIL"];
    private static readonly HashSet<string> EvidenceTypes =
    [
        "BROWSER",
        "TEST_RUN",
        "BUILD",
        "STATIC_INSPECTION",
        "API",
        "DATABASE",
        "RUNTIME",
        "SOURCE_INSPECTION",
        "MANUAL_JUDGMENT",
        "NOT_APPLICABLE",
    ];

    public static V3ValidationEvidenceGateResult Validate(
        string output,
        V3ValidationReport report,
        bool uiRequired)
    {
        if (!TryExtractManifest(output, out var manifest, out var parseError))
        {
            return V3ValidationEvidenceGateResult.Invalid(parseError ?? "validation_manifest_missing");
        }

        var errors = new List<string>();
        var requirementExpected = report.RequirementsChecked;
        var checklistExpected = report.ChecklistTotal;

        if (!string.Equals(manifest.ManifestContractVersion, ContractVersion, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"manifest_contract_version:{manifest.ManifestContractVersion ?? "missing"}");
        }
        if (manifest.Requirements.Count != requirementExpected)
        {
            errors.Add($"requirements_count:{manifest.Requirements.Count}/{requirementExpected}");
        }
        if (manifest.Checklist.Count != checklistExpected)
        {
            errors.Add($"checklist_count:{manifest.Checklist.Count}/{checklistExpected}");
        }
        AddDuplicateErrors(errors, manifest.Requirements.Select(item => item.RequirementId), "requirement_duplicate");
        AddDuplicateErrors(errors, manifest.Checklist.Select(item => item.CheckId), "checklist_duplicate");

        foreach (var item in manifest.Requirements)
        {
            ValidateStatus(errors, item.Status, $"requirement_status:{item.RequirementId}");
            if (string.IsNullOrWhiteSpace(item.EvidenceReference))
            {
                errors.Add($"requirement_evidence_missing:{item.RequirementId}");
            }
        }

        foreach (var item in manifest.Checklist)
        {
            ValidateStatus(errors, item.Status, $"checklist_status:{item.CheckId}");
            if (!EvidenceTypes.Contains(item.EvidenceType))
            {
                errors.Add($"checklist_evidence_type:{item.CheckId}");
            }
            if (item.Status == "N_A" && string.IsNullOrWhiteSpace(item.Notes))
            {
                errors.Add($"checklist_na_reason_missing:{item.CheckId}");
            }
            if (item.Status is "PASS" or "FIXED" && string.IsNullOrWhiteSpace(item.EvidenceReference))
            {
                errors.Add($"checklist_evidence_missing:{item.CheckId}");
            }
            if (item.Status == "FAIL")
            {
                errors.Add($"checklist_fail:{item.CheckId}");
            }
        }

        if (uiRequired)
        {
            if (manifest.BrowserRuns.Count == 0)
            {
                errors.Add("browser_evidence_missing");
            }
            else if (!manifest.BrowserRuns.Any(run => run.ExitCode == 0 && run.Failed == 0))
            {
                errors.Add("browser_evidence_not_passing");
            }

            if (manifest.HandoffReadiness is null)
            {
                errors.Add("handoff_readiness_missing");
            }
            else
            {
                if (!manifest.HandoffReadiness.RuntimeReachable)
                {
                    errors.Add("handoff_runtime_unreachable");
                }
                if (string.IsNullOrWhiteSpace(manifest.HandoffReadiness.ApplicationUrl))
                {
                    errors.Add("handoff_application_url_missing");
                }
                if (!manifest.HandoffReadiness.CleanAcceptanceEnvironment)
                {
                    errors.Add("handoff_environment_not_clean");
                }
            }
        }

        return errors.Count == 0
            ? V3ValidationEvidenceGateResult.Valid(manifest)
            : V3ValidationEvidenceGateResult.Invalid(string.Join(";", errors), manifest);
    }

    private static void ValidateStatus(List<string> errors, string status, string code)
    {
        if (!Statuses.Contains(status))
        {
            errors.Add(code);
        }
    }

    private static void AddDuplicateErrors(List<string> errors, IEnumerable<string> ids, string prefix)
    {
        foreach (var duplicate in ids.GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            errors.Add($"{prefix}:{duplicate.Key}");
        }
    }

    private static bool TryExtractManifest(
        string output,
        out V3ValidationResultManifest manifest,
        out string? error)
    {
        manifest = new V3ValidationResultManifest("unknown", "unknown", "unknown", "unknown", "unknown", [], [], [], null);
        error = null;
        var marker = output.IndexOf("POSEIDON_VALIDATION_MANIFEST", StringComparison.Ordinal);
        if (marker < 0)
        {
            error = "validation_manifest_missing";
            return false;
        }

        var jsonStart = output.IndexOf('{', marker);
        if (jsonStart < 0)
        {
            error = "validation_manifest_json_missing";
            return false;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = jsonStart; i < output.Length; i++)
        {
            var c = output[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (c == '\\' && inString)
            {
                escaped = true;
                continue;
            }
            if (c == '"')
            {
                inString = !inString;
                continue;
            }
            if (inString) continue;
            if (c == '{') depth++;
            if (c == '}') depth--;
            if (depth == 0)
            {
                try
                {
                    manifest = JsonSerializer.Deserialize<V3ValidationResultManifest>(
                        output.Substring(jsonStart, i - jsonStart + 1),
                        Json) ?? manifest;
                    return true;
                }
                catch (JsonException exception)
                {
                    error = $"validation_manifest_invalid_json:{exception.GetType().Name}";
                    return false;
                }
            }
        }

        error = "validation_manifest_json_unclosed";
        return false;
    }
}

public sealed record V3ValidationEvidenceGateResult(
    bool Accepted,
    string? Reason,
    V3ValidationResultManifest? Manifest)
{
    public static V3ValidationEvidenceGateResult Valid(V3ValidationResultManifest manifest) =>
        new(true, null, manifest);

    public static V3ValidationEvidenceGateResult Invalid(string reason, V3ValidationResultManifest? manifest = null) =>
        new(false, reason, manifest);
}

public sealed record V3ValidationResultManifest(
    string ManifestContractVersion,
    string ChecklistVersion,
    string ChecklistSha256,
    string MissionId,
    string ExecutionId,
    IReadOnlyList<V3RequirementResult> Requirements,
    IReadOnlyList<V3CheckResult> Checklist,
    IReadOnlyList<V3BrowserRunResult> BrowserRuns,
    V3HandoffReadinessResult? HandoffReadiness);

public sealed record V3RequirementResult(
    string RequirementId,
    string Status,
    string EvidenceReference,
    string? Notes);

public sealed record V3CheckResult(
    string CheckId,
    string Status,
    string EvidenceType,
    string EvidenceReference,
    string? Notes,
    DateTimeOffset ExecutedAt);

public sealed record V3BrowserRunResult(
    string Runner,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int ExitCode,
    string BaseUrl,
    IReadOnlyList<string> Viewports,
    IReadOnlyList<string> TestFiles,
    int Passed,
    int Failed,
    int Skipped,
    int ConsoleErrors,
    int NetworkErrors);

public sealed record V3HandoffReadinessResult(
    string? ApplicationUrl,
    bool RuntimeReachable,
    bool HealthPass,
    bool CleanAcceptanceEnvironment,
    bool AccessInformationCaptured,
    bool TestCredentialsCapturedWhenApplicable);
