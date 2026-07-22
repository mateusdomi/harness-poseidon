using Harness.Host.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// Continuação governada: o artifact arquivado de uma tentativa reprovada só pode ser
/// recuperado se o checksum do patch bater com o manifest. Qualquer adulteração, ausência ou
/// escape de diretório é um código de recusa tipado — nunca um patch confiado às cegas.
/// </summary>
public sealed class AttemptArtifactArchiveTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"poseidon-archive-{Guid.NewGuid():N}");

    private ArchivedAttemptManifest SampleManifest() => new()
    {
        AttemptId = "01KY36MZMV5YFDHW9EWFZMK0CM",
        TenantId = "01KY000000000000000000TEN0",
        ProjectId = "01KY36JQ8A48Q2TVYJMA5Q1N4F",
        TaskId = "01KY36KYHVWB0ZYWCZQPBQZ1ZP",
        Role = "frontend-specialist",
        ActorAlias = "worker-codex-frontend",
        ControlledRepositoryRoot = _root,
        SourceCommit = "6e4f71378bdd231ecc309e68ca82c2f55abde1e8",
        PatchFileName = "attempt.patch",
        PatchSha256 = "unset",
        Verdict = "fail",
        ScopeClaims = ["frontend/**", "docs/frontend/**"],
        Findings = [new ArchivedAttemptFinding("P0", "suite-vermelha", "1 teste falhando")],
        ArchivedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void WriteThenLoadRoundtripsWithASealedChecksum()
    {
        var archive = new AttemptArtifactArchive(_root);
        var sealedManifest = archive.Write(SampleManifest(), "the patch body");

        Assert.Equal(AttemptArtifactArchive.Sha256Hex("the patch body"), sealedManifest.PatchSha256);

        var loaded = archive.TryLoad("01KY36MZMV5YFDHW9EWFZMK0CM");
        Assert.True(loaded.Ok);
        Assert.Equal("archive.loaded", loaded.ReasonCode);
        Assert.Equal("the patch body", loaded.PatchContent);
        Assert.Equal("fail", loaded.Manifest!.Verdict);
    }

    [Fact]
    public void ATamperedPatchIsRejectedByChecksum()
    {
        var archive = new AttemptArtifactArchive(_root);
        archive.Write(SampleManifest(), "original body");

        // O patch é alterado por fora, sem atualizar o manifest.
        File.WriteAllText(Path.Combine(_root, "attempt.patch"), "tampered body");

        var loaded = archive.TryLoad("01KY36MZMV5YFDHW9EWFZMK0CM");
        Assert.False(loaded.Ok);
        Assert.Equal("archive.checksum_mismatch", loaded.ReasonCode);
    }

    [Fact]
    public void AMissingManifestIsATypedRefusal()
    {
        var archive = new AttemptArtifactArchive(_root);
        var loaded = archive.TryLoad("01KY36MZMV5YFDHW9EWFZMK0CM");
        Assert.False(loaded.Ok);
        Assert.Equal("archive.manifest_missing", loaded.ReasonCode);
    }

    [Fact]
    public void AMissingPatchIsATypedRefusal()
    {
        var archive = new AttemptArtifactArchive(_root);
        archive.Write(SampleManifest(), "body");
        File.Delete(Path.Combine(_root, "attempt.patch"));

        var loaded = archive.TryLoad("01KY36MZMV5YFDHW9EWFZMK0CM");
        Assert.False(loaded.Ok);
        Assert.Equal("archive.patch_missing", loaded.ReasonCode);
    }

    [Fact]
    public void APatchNameThatEscapesTheArchiveRootIsRejected()
    {
        var archive = new AttemptArtifactArchive(_root);
        Assert.Throws<ArgumentException>(() =>
            archive.Write(SampleManifest() with { PatchFileName = "../escape.patch" }, "body"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
