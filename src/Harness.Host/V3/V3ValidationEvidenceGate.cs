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
        bool uiRequired,
        string? repository = null,
        string? expectedMissionId = null,
        string? expectedExecutionId = null)
    {
        if (!TryExtractManifest(output, repository, out var manifest, out var parseError))
        {
            return V3ValidationEvidenceGateResult.Invalid(parseError ?? "validation_manifest_missing");
        }
        manifest = ExpandReferencedManifestIfPresent(manifest, repository) ?? manifest;

        var errors = new List<string>();
        var requirementExpected = report.RequirementsChecked;
        var checklistExpected = report.ChecklistTotal;

        if (!string.Equals(manifest.ManifestContractVersion, ContractVersion, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"manifest_contract_version:{manifest.ManifestContractVersion ?? "missing"}");
        }
        if (!string.IsNullOrWhiteSpace(expectedMissionId) &&
            !string.Equals(manifest.MissionId, expectedMissionId, StringComparison.Ordinal))
        {
            errors.Add($"manifest_mission_id:{manifest.MissionId ?? "missing"}");
        }
        if (!string.IsNullOrWhiteSpace(expectedExecutionId) &&
            !string.Equals(manifest.ExecutionId, expectedExecutionId, StringComparison.Ordinal))
        {
            errors.Add($"manifest_execution_id:{manifest.ExecutionId ?? "missing"}");
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
            if (!IsSupportedEvidenceType(item.EvidenceType))
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

    private static bool IsSupportedEvidenceType(string evidenceType)
    {
        if (string.IsNullOrWhiteSpace(evidenceType)) return false;
        var parts = evidenceType
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeEvidenceTypePart)
            .ToArray();
        return parts.Length > 0 && parts.All(part => part is not null && EvidenceTypes.Contains(part));
    }

    private static string? NormalizeEvidenceTypePart(string value) =>
        value.Trim().ToUpperInvariant().Replace('-', '_').Replace(' ', '_') switch
        {
            "BROWSER" => "BROWSER",
            "TEST" => "TEST_RUN",
            "TEST_RUN" => "TEST_RUN",
            "BUILD" => "BUILD",
            "STATIC_INSPECTION" => "STATIC_INSPECTION",
            "INSPECTION" => "STATIC_INSPECTION",
            "API" => "API",
            "DATABASE" => "DATABASE",
            "RUNTIME" => "RUNTIME",
            "SOURCE_INSPECTION" => "SOURCE_INSPECTION",
            "REQUIREMENTS" => "SOURCE_INSPECTION",
            "MANUAL_JUDGMENT" => "MANUAL_JUDGMENT",
            "NOT_APPLICABLE" => "NOT_APPLICABLE",
            _ => null,
        };

    private static void AddDuplicateErrors(List<string> errors, IEnumerable<string> ids, string prefix)
    {
        foreach (var duplicate in ids.GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            errors.Add($"{prefix}:{duplicate.Key}");
        }
    }

    private static bool TryExtractManifest(
        string output,
        string? repository,
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
                    var json = output.Substring(jsonStart, i - jsonStart + 1);
                    try
                    {
                        manifest = JsonSerializer.Deserialize<V3ValidationResultManifest>(
                            json,
                            Json) ?? manifest;
                        return true;
                    }
                    catch (JsonException exception)
                    {
                        if (TryResolveReferencedManifest(json, repository, out manifest, out var referenceError))
                        {
                            return true;
                        }

                        error = referenceError ?? $"validation_manifest_invalid_json:{exception.GetType().Name}";
                        return false;
                    }
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

    private static bool TryResolveReferencedManifest(
        string json,
        string? repository,
        out V3ValidationResultManifest manifest,
        out string? error)
    {
        manifest = new V3ValidationResultManifest("unknown", "unknown", "unknown", "unknown", "unknown", [], [], [], null);
        error = null;
        if (string.IsNullOrWhiteSpace(repository))
        {
            error = "validation_manifest_invalid_json:JsonException";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var reference = FindCanonicalItemsReference(document.RootElement);
            if (string.IsNullOrWhiteSpace(reference))
            {
                error = "validation_manifest_invalid_json:JsonException";
                return false;
            }

            var path = ResolveReferencePath(repository, reference);
            if (path is null || !File.Exists(path))
            {
                error = "validation_manifest_reference_unreadable";
                return false;
            }

            manifest = JsonSerializer.Deserialize<V3ValidationResultManifest>(
                File.ReadAllText(path),
                Json) ?? manifest;
            return true;
        }
        catch (JsonException exception)
        {
            error = $"validation_manifest_invalid_json:{exception.GetType().Name}";
            return false;
        }
        catch (IOException)
        {
            error = "validation_manifest_reference_unreadable";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = "validation_manifest_reference_unreadable";
            return false;
        }
    }

    private static string? FindCanonicalItemsReference(JsonElement root)
    {
        if (root.TryGetProperty("canonicalManifest", out var canonicalManifest) &&
            canonicalManifest.ValueKind == JsonValueKind.String)
        {
            return canonicalManifest.GetString();
        }

        foreach (var propertyName in new[] { "requirements", "checklist", "browserRuns" })
        {
            if (root.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.Object &&
                TryGetReferenceProperty(value, out var reference))
            {
                return reference;
            }
        }

        return null;
    }

    private static bool TryGetReferenceProperty(JsonElement value, out string? reference)
    {
        foreach (var propertyName in new[] { "canonicalItemsReference", "itemsReference" })
        {
            if (value.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                reference = property.GetString();
                return true;
            }
        }

        reference = null;
        return false;
    }

    private static string? ResolveReferencePath(string repository, string reference)
    {
        var pathPart = reference.Split('#', 2)[0];
        if (string.IsNullOrWhiteSpace(pathPart)) return null;
        var repositoryRoot = Path.GetFullPath(repository);
        var candidate = Path.IsPathRooted(pathPart)
            ? Path.GetFullPath(pathPart)
            : Path.GetFullPath(Path.Combine(repositoryRoot, pathPart));
        return candidate.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
               string.Equals(candidate, repositoryRoot, StringComparison.Ordinal)
            ? candidate
            : null;
    }

    private static V3ValidationResultManifest? ExpandReferencedManifestIfPresent(
        V3ValidationResultManifest manifest,
        string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return null;
        foreach (var reference in manifest.Checklist.Select(item => item.EvidenceReference)
                     .Concat(manifest.Requirements.Select(item => item.EvidenceReference)))
        {
            if (string.IsNullOrWhiteSpace(reference) ||
                !reference.Contains(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = ResolveReferencePath(repository, reference);
            if (path is null || !File.Exists(path)) continue;
            try
            {
                var candidate = JsonSerializer.Deserialize<V3ValidationResultManifest>(
                    File.ReadAllText(path),
                    Json);
                if (candidate is not null &&
                    string.Equals(candidate.ManifestContractVersion, manifest.ManifestContractVersion, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.MissionId, manifest.MissionId, StringComparison.Ordinal) &&
                    string.Equals(candidate.ExecutionId, manifest.ExecutionId, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            catch (JsonException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
        }

        return null;
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
