using Harness.Host.V3;

namespace Harness.UnitTests.V3;

public sealed class V3ValidationEvidenceGateTests
{
    [Fact]
    public void MarkerOnlyIsRejected()
    {
        var result = V3ValidationEvidenceGate.Validate(
            "POSEIDON_VALIDATION_COMPLETE",
            Report(),
            uiRequired: true);

        Assert.False(result.Accepted);
        Assert.Equal("validation_manifest_missing", result.Reason);
    }

    [Fact]
    public void SummaryOnlyIsRejected()
    {
        var result = V3ValidationEvidenceGate.Validate(
            """
            ChecklistTotal: 2
            ChecklistFail: 0
            POSEIDON_VALIDATION_COMPLETE
            """,
            Report(),
            uiRequired: true);

        Assert.False(result.Accepted);
        Assert.Equal("validation_manifest_missing", result.Reason);
    }

    [Fact]
    public void MissingChecklistItemIsRejected()
    {
        var result = V3ValidationEvidenceGate.Validate(Manifest(checklistCount: 1), Report(), uiRequired: true);

        Assert.False(result.Accepted);
        Assert.Contains("checklist_count:1/2", result.Reason);
    }

    [Fact]
    public void MissingManifestContractVersionIsRejected()
    {
        var result = V3ValidationEvidenceGate.Validate(
            Manifest(manifestContractVersion: null),
            Report(),
            uiRequired: true);

        Assert.False(result.Accepted);
        Assert.Contains("manifest_contract_version:missing", result.Reason);
    }

    [Fact]
    public void WrongManifestContractVersionIsRejected()
    {
        var result = V3ValidationEvidenceGate.Validate(
            Manifest(manifestContractVersion: "v3.validation.1"),
            Report(),
            uiRequired: true);

        Assert.False(result.Accepted);
        Assert.Contains("manifest_contract_version:v3.validation.1", result.Reason);
    }

    [Fact]
    public void MismatchedMissionIdentityIsRejectedWhenExpected()
    {
        var result = V3ValidationEvidenceGate.Validate(
            Manifest(missionId: "wrong-mission", executionId: "wrong-execution"),
            Report(),
            uiRequired: true,
            expectedMissionId: "m",
            expectedExecutionId: "e");

        Assert.False(result.Accepted);
        Assert.Contains("manifest_mission_id:wrong-mission", result.Reason);
        Assert.Contains("manifest_execution_id:wrong-execution", result.Reason);
    }

    [Fact]
    public void CanonicalManifestFileReferenceIsAcceptedWhenInsideRepository()
    {
        var repository = Path.Combine(Path.GetTempPath(), $"poseidon-validation-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repository, "docs"));
        try
        {
            File.WriteAllText(Path.Combine(repository, "docs", "manifest.json"), FullManifestJson());
            var output = """
            POSEIDON_VALIDATION_MANIFEST
            {
              "manifestContractVersion":"v3.validation.2",
              "checklistVersion":"test",
              "checklistSha256":"sha256:test",
              "missionId":"m",
              "executionId":"e",
              "requirements":{"canonicalItemsReference":"docs/manifest.json#/requirements"},
              "checklist":{"canonicalItemsReference":"docs/manifest.json#/checklist"},
              "browserRuns":{"canonicalItemsReference":"docs/manifest.json#/browserRuns"},
              "handoffReadiness":{"applicationUrl":"http://localhost:5000","runtimeReachable":true,"healthPass":true,"cleanAcceptanceEnvironment":true,"accessInformationCaptured":true,"testCredentialsCapturedWhenApplicable":true}
            }
            POSEIDON_VALIDATION_COMPLETE
            """;

            var result = V3ValidationEvidenceGate.Validate(
                output,
                Report(),
                uiRequired: true,
                repository: repository,
                expectedMissionId: "m",
                expectedExecutionId: "e");

            Assert.True(result.Accepted, result.Reason);
        }
        finally
        {
            if (Directory.Exists(repository))
            {
                Directory.Delete(repository, recursive: true);
            }
        }
    }

    [Fact]
    public void CanonicalManifestShortcutIsAcceptedWhenInsideRepository()
    {
        var repository = Path.Combine(Path.GetTempPath(), $"poseidon-validation-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repository, "docs"));
        try
        {
            File.WriteAllText(Path.Combine(repository, "docs", "manifest.json"), FullManifestJson());
            var output = """
            POSEIDON_VALIDATION_MANIFEST
            {
              "manifestContractVersion":"v3.validation.2",
              "checklistVersion":"test",
              "checklistSha256":"sha256:test",
              "missionId":"m",
              "executionId":"e",
              "requirements":"see canonical manifest",
              "checklist":"see canonical manifest",
              "browserRuns":"see canonical manifest",
              "handoffReadiness":{"applicationUrl":"http://localhost:5000","runtimeReachable":true,"healthPass":true,"cleanAcceptanceEnvironment":true,"accessInformationCaptured":true,"testCredentialsCapturedWhenApplicable":true},
              "canonicalManifest":"docs/manifest.json"
            }
            POSEIDON_VALIDATION_COMPLETE
            """;

            var result = V3ValidationEvidenceGate.Validate(
                output,
                Report(),
                uiRequired: true,
                repository: repository,
                expectedMissionId: "m",
                expectedExecutionId: "e");

            Assert.True(result.Accepted, result.Reason);
        }
        finally
        {
            if (Directory.Exists(repository))
            {
                Directory.Delete(repository, recursive: true);
            }
        }
    }

    [Fact]
    public void DuplicateChecklistItemIsRejected()
    {
        var result = V3ValidationEvidenceGate.Validate(
            Manifest(checkIds: ["Q001", "Q001"]),
            Report(),
            uiRequired: true);

        Assert.False(result.Accepted);
        Assert.Contains("checklist_duplicate:Q001", result.Reason);
    }

    [Fact]
    public void KnownCompositeEvidenceTypeAliasesAreAccepted()
    {
        var result = V3ValidationEvidenceGate.Validate(
            Manifest(firstEvidenceType: "browser/runtime", secondEvidenceType: "inspection/test"),
            Report(),
            uiRequired: true);

        Assert.True(result.Accepted, result.Reason);
    }

    [Fact]
    public void UnknownEvidenceTypeIsRejected()
    {
        var result = V3ValidationEvidenceGate.Validate(
            Manifest(firstEvidenceType: "storytelling"),
            Report(),
            uiRequired: true);

        Assert.False(result.Accepted);
        Assert.Contains("checklist_evidence_type:Q001", result.Reason);
    }

    [Fact]
    public void NotApplicableWithoutReasonIsRejected()
    {
        var result = V3ValidationEvidenceGate.Validate(
            Manifest(secondStatus: "N_A", secondNotes: null),
            Report(),
            uiRequired: true);

        Assert.False(result.Accepted);
        Assert.Contains("checklist_na_reason_missing:Q002", result.Reason);
    }

    [Fact]
    public void UiProjectWithoutBrowserEvidenceIsRejected()
    {
        var result = V3ValidationEvidenceGate.Validate(
            Manifest(browser: false),
            Report(),
            uiRequired: true);

        Assert.False(result.Accepted);
        Assert.Contains("browser_evidence_missing", result.Reason);
    }

    [Fact]
    public void ChecklistFailIsRejected()
    {
        var result = V3ValidationEvidenceGate.Validate(
            Manifest(secondStatus: "FAIL"),
            Report() with { ChecklistFail = 1 },
            uiRequired: true);

        Assert.False(result.Accepted);
        Assert.Contains("checklist_fail:Q002", result.Reason);
    }

    [Fact]
    public void CompleteManifestIsAccepted()
    {
        var result = V3ValidationEvidenceGate.Validate(Manifest(), Report(), uiRequired: true);

        Assert.True(result.Accepted);
        Assert.NotNull(result.Manifest);
    }

    [Fact]
    public void NonUiProjectDoesNotRequireBrowser()
    {
        var result = V3ValidationEvidenceGate.Validate(
            Manifest(browser: false, handoff: false),
            Report(),
            uiRequired: false);

        Assert.True(result.Accepted);
    }

    private static V3ValidationReport Report() =>
        new(
            RequirementsChecked: 2,
            RequirementsPassed: 2,
            RequirementsFailed: 0,
            ChecklistTotal: 2,
            ChecklistPass: 2,
            ChecklistFixed: 0,
            ChecklistNA: 0,
            ChecklistFail: 0,
            BrowserTestsPassed: 1,
            BrowserTestsFailed: 0,
            BrowserTestsSkipped: 0,
            BugsFound: 0,
            BugsFixed: 0,
            BugsRemaining: 0,
            FinalHead: "abc");

    private static string Manifest(
        int checklistCount = 2,
        string[]? checkIds = null,
        string secondStatus = "PASS",
        string? secondNotes = "verified",
        bool browser = true,
        bool handoff = true,
        string? manifestContractVersion = V3ValidationEvidenceGate.ContractVersion,
        string missionId = "m",
        string executionId = "e",
        string firstEvidenceType = "BROWSER",
        string secondEvidenceType = "BROWSER")
    {
        checkIds ??= ["Q001", "Q002"];
        var checklist = string.Join(
            ",",
            Enumerable.Range(0, checklistCount).Select(index =>
            {
                var id = checkIds[Math.Min(index, checkIds.Length - 1)];
                var status = index == 1 ? secondStatus : "PASS";
                var notes = index == 1 ? secondNotes : "verified";
                var evidenceType = index == 1 ? secondEvidenceType : firstEvidenceType;
                return $$"""
                {"checkId":"{{id}}","status":"{{status}}","evidenceType":"{{(status == "N_A" ? "NOT_APPLICABLE" : evidenceType)}}","evidenceReference":"run#1","notes":{{(notes is null ? "null" : $"\"{notes}\"")}},"executedAt":"2026-08-12T00:00:00Z"}
                """;
            }));

        var browserRuns = browser
            ? """
              [{"runner":"playwright","startedAt":"2026-08-12T00:00:00Z","completedAt":"2026-08-12T00:01:00Z","exitCode":0,"baseUrl":"http://localhost:5000","viewports":["desktop"],"testFiles":["e2e.spec.ts"],"passed":1,"failed":0,"skipped":0,"consoleErrors":0,"networkErrors":0}]
              """
            : "[]";
        var handoffReadiness = handoff
            ? """
              {"applicationUrl":"http://localhost:5000","runtimeReachable":true,"healthPass":true,"cleanAcceptanceEnvironment":true,"accessInformationCaptured":true,"testCredentialsCapturedWhenApplicable":true}
              """
            : "null";

        return $$"""
        POSEIDON_VALIDATION_MANIFEST
        {
          {{(manifestContractVersion is null ? "" : $"\"manifestContractVersion\":\"{manifestContractVersion}\",")}}
          "checklistVersion":"test",
          "checklistSha256":"sha256:test",
          "missionId":"{{missionId}}",
          "executionId":"{{executionId}}",
          "requirements":[
            {"requirementId":"AC01","status":"PASS","evidenceReference":"run#1","notes":"ok"},
            {"requirementId":"AC02","status":"PASS","evidenceReference":"run#1","notes":"ok"}
          ],
          "checklist":[{{checklist}}],
          "browserRuns":{{browserRuns}},
          "handoffReadiness":{{handoffReadiness}}
        }
        POSEIDON_VALIDATION_COMPLETE
        """;
    }

    private static string FullManifestJson()
    {
        var manifest = Manifest();
        var start = manifest.IndexOf('{');
        var end = manifest.LastIndexOf('}');
        return manifest.Substring(start, end - start + 1);
    }
}
