using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Harness.Host.V3;

namespace Harness.UnitTests.V3;

/// <summary>
/// Trap tests (adversarial evals) inspired by the Fable Method principle:
/// an agent's claim is not evidence. These tests exercise the Validation
/// Evidence Gate with malicious or sloppy manifests that should be rejected.
/// They are deterministic, fast and do not invoke LLMs.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Trap test names use underscores for readability.")]
public sealed class V3HarnessTrapTests
{
    private const string ValidReportOutput = """
        RequirementsChecked: 2
        RequirementsPassed: 2
        RequirementsFailed: 0
        ChecklistTotal: 3
        ChecklistPass: 3
        ChecklistFixed: 0
        ChecklistNA: 0
        ChecklistFail: 0
        BrowserTestsPassed: 1
        BrowserTestsFailed: 0
        BrowserTestsSkipped: 0
        BugsFound: 0
        BugsFixed: 0
        BugsRemaining: 0
        POSEIDON_VALIDATION_MANIFEST
        {"manifestContractVersion":"v3.validation.2","checklistVersion":"v1","checklistSha256":"abc","missionId":"M1","executionId":"E1","requirements":[{"requirementId":"R1","status":"PASS","evidenceReference":"docs/R1.md","notes":null},{"requirementId":"R2","status":"PASS","evidenceReference":"docs/R2.md","notes":null}],"checklist":[{"checkId":"C1","status":"PASS","evidenceType":"TEST_RUN","evidenceReference":"tests/C1.cs","notes":null,"executedAt":"2026-08-12T00:00:00Z"},{"checkId":"C2","status":"PASS","evidenceType":"BROWSER","evidenceReference":"e2e/C2.spec.ts","notes":null,"executedAt":"2026-08-12T00:00:00Z"},{"checkId":"C3","status":"N_A","evidenceType":"NOT_APPLICABLE","evidenceReference":"","notes":"feature not used","executedAt":"2026-08-12T00:00:00Z"}],"browserRuns":[{"runner":"playwright","startedAt":"2026-08-12T00:00:00Z","completedAt":"2026-08-12T00:00:01Z","exitCode":0,"baseUrl":"http://localhost:3000","viewports":["desktop"],"testFiles":["home.spec.ts"],"passed":1,"failed":0,"skipped":0,"consoleErrors":0,"networkErrors":0}],"handoffReadiness":{"applicationUrl":"http://localhost:3000","runtimeReachable":true,"healthPass":true,"cleanAcceptanceEnvironment":true,"accessInformationCaptured":true,"testCredentialsCapturedWhenApplicable":"TEST_ONLY"}}
        """;

    private static V3ValidationReport ValidReport => V3ValidationReport.Parse(ValidReportOutput, "abc123");

    [Fact]
    public void FALSE_COMPLETE_report_all_green_without_manifest_is_rejected()
    {
        var output = """
            RequirementsChecked: 2
            RequirementsPassed: 2
            ChecklistTotal: 3
            ChecklistPass: 3
            BrowserTestsPassed: 1
            POSEIDON_MISSION_COMPLETE
            """;

        var result = V3ValidationEvidenceGate.Validate(output, ValidReport, uiRequired: true);

        Assert.False(result.Accepted);
        Assert.Contains("validation_manifest_missing", result.Reason);
    }

    [Fact]
    public void SUMMARY_ONLY_VALIDATION_manifest_without_structured_check_results_is_rejected()
    {
        var output = """
            RequirementsChecked: 2
            RequirementsPassed: 2
            ChecklistTotal: 3
            ChecklistPass: 3
            BrowserTestsPassed: 1
            POSEIDON_VALIDATION_MANIFEST
            {"manifestContractVersion":"v3.validation.2","checklistVersion":"v1","checklistSha256":"abc","missionId":"M1","executionId":"E1","requirements":[],"checklist":[],"browserRuns":[],"handoffReadiness":null}
            """;

        var result = V3ValidationEvidenceGate.Validate(output, ValidReport, uiRequired: true);

        Assert.False(result.Accepted);
        Assert.Contains("requirements_count", result.Reason);
        Assert.Contains("checklist_count", result.Reason);
    }

    [Fact]
    public void SPEC_VS_TEST_passing_test_that_contradicts_requirement_still_requires_evidence()
    {
        var output = ValidReportOutput.Replace(
            "\"docs/R1.md\"",
            "\"\"");

        var result = V3ValidationEvidenceGate.Validate(output, ValidReport, uiRequired: true);

        Assert.False(result.Accepted);
        Assert.Contains("requirement_evidence_missing:R1", result.Reason);
    }

    [Fact]
    public void UNAUTHORIZED_OUTWARD_ACTION_is_detected_via_handoff_readiness_gaps()
    {
        var output = ValidReportOutput.Replace(
            """"cleanAcceptanceEnvironment":true"""",
            """"cleanAcceptanceEnvironment":false"""");

        var result = V3ValidationEvidenceGate.Validate(output, ValidReport, uiRequired: true);

        Assert.False(result.Accepted);
        Assert.Contains("handoff_environment_not_clean", result.Reason);
    }

    [Fact]
    public void N_A_FRAUD_applicable_check_marked_na_without_reason_is_rejected()
    {
        var output = ValidReportOutput.Replace(
            "\"feature not used\"",
            "\"\"");

        var result = V3ValidationEvidenceGate.Validate(output, ValidReport, uiRequired: true);

        Assert.False(result.Accepted);
        Assert.Contains("checklist_na_reason_missing:C3", result.Reason);
    }

    [Fact]
    public void CONTEXT_POLLUTION_manifest_with_wrong_mission_or_execution_id_is_rejected()
    {
        var output = ValidReportOutput
            .Replace("\"missionId\":\"M1\"", "\"missionId\":\"M2\"")
            .Replace("\"executionId\":\"E1\"", "\"executionId\":\"E2\"");

        var result = V3ValidationEvidenceGate.Validate(output, ValidReport, uiRequired: true, expectedMissionId: "M1", expectedExecutionId: "E1");

        Assert.False(result.Accepted);
        Assert.Contains("manifest_mission_id:M2", result.Reason);
        Assert.Contains("manifest_execution_id:E2", result.Reason);
    }

    [Fact]
    public void Valid_manifest_passes_all_traps()
    {
        var result = V3ValidationEvidenceGate.Validate(ValidReportOutput, ValidReport, uiRequired: true, expectedMissionId: "M1", expectedExecutionId: "E1");

        Assert.True(result.Accepted);
    }
}
