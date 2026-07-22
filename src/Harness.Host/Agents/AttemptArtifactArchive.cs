using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Host.Agents;

/// <summary>
/// Um achado do critic preservado na provenance do artifact arquivado. É o registro
/// durável do motivo pelo qual a tentativa foi reprovada — a continuação o consome como
/// critério de aceite, nunca o reinventa.
/// </summary>
public sealed record ArchivedAttemptFinding(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("summary")] string Summary);

/// <summary>
/// Manifest de provenance de uma tentativa arquivada fora do repositório.
///
/// Quando uma tentativa é reprovada pelo critic, o trabalho NÃO entra em <c>develop</c>: o
/// diff é arquivado como patch, e este manifest amarra o patch ao seu checksum, ao receipt
/// de governança, ao review que o reprovou e à identidade da tentativa (tenant, projeto,
/// tarefa, papel, actor, escopo). É o único registro durável necessário para uma
/// continuação governada — não depende de linha de banco que possa ter sido descartada.
/// </summary>
public sealed record ArchivedAttemptManifest
{
    /// <summary>Versão do schema deste manifest. Fechado; drift é rejeitado.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("attemptId")]
    public required string AttemptId { get; init; }

    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    [JsonPropertyName("projectId")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("taskId")]
    public required string TaskId { get; init; }

    /// <summary>Papel lógico da tentativa. A continuação precisa do mesmo papel.</summary>
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    /// <summary>Alias da conta que produziu o trabalho. Nunca e-mail nem credencial.</summary>
    [JsonPropertyName("actorAlias")]
    public required string ActorAlias { get; init; }

    /// <summary>
    /// Raiz do repositório controlado onde o trabalho foi produzido. A continuação recusa
    /// um repositório diferente: um patch não é portável entre árvores arbitrárias.
    /// </summary>
    [JsonPropertyName("controlledRepositoryRoot")]
    public required string ControlledRepositoryRoot { get; init; }

    /// <summary>Commit do worker (identidade do actor) que originou o patch.</summary>
    [JsonPropertyName("sourceCommit")]
    public required string SourceCommit { get; init; }

    [JsonPropertyName("patchFileName")]
    public required string PatchFileName { get; init; }

    /// <summary>SHA-256 hex minúsculo do conteúdo bruto do patch.</summary>
    [JsonPropertyName("patchSha256")]
    public required string PatchSha256 { get; init; }

    /// <summary>Turno de receipt de governança da tentativa arquivada, se houve.</summary>
    [JsonPropertyName("receiptTurnId")]
    public string? ReceiptTurnId { get; init; }

    /// <summary>Review do critic que produziu o veredito. Prova de avaliação independente.</summary>
    [JsonPropertyName("reviewId")]
    public string? ReviewId { get; init; }

    /// <summary>Veredito do critic. Só se continua o que foi <c>fail</c>.</summary>
    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }

    /// <summary>Escopo autorizado da tentativa arquivada.</summary>
    [JsonPropertyName("scopeClaims")]
    public IReadOnlyList<string> ScopeClaims { get; init; } = [];

    /// <summary>Achados do critic. Viram critérios de aceite da continuação.</summary>
    [JsonPropertyName("findings")]
    public IReadOnlyList<ArchivedAttemptFinding> Findings { get; init; } = [];

    [JsonPropertyName("archivedAt")]
    public required DateTimeOffset ArchivedAt { get; init; }
}

/// <summary>Resultado tipado de uma tentativa de carregar um artifact arquivado.</summary>
public sealed record ArchiveLoadResult(
    bool Ok,
    string ReasonCode,
    ArchivedAttemptManifest? Manifest,
    string? PatchPath,
    string? PatchContent)
{
    public static ArchiveLoadResult Fail(string reasonCode) => new(false, reasonCode, null, null, null);
}

/// <summary>
/// Arquivo governado de artifacts de tentativas fora do repositório (por padrão
/// <c>~/.harness/pilots</c>). Um patch reprovado é preservado aqui com seu manifest; a
/// continuação o recupera e VALIDA o checksum antes de confiar nele.
///
/// O trabalho reprovado nunca vive em <c>develop</c>; este arquivo é o lugar recuperável.
/// </summary>
public sealed class AttemptArtifactArchive
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;

    public AttemptArtifactArchive(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    /// <summary>Raiz padrão: <c>~/.harness/pilots</c>. Fora do repositório, por regra.</summary>
    public static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".harness",
            "pilots");

    public string Root => _root;

    private string ManifestPath(string attemptId) =>
        Path.Combine(_root, $"{attemptId}.manifest.json");

    /// <summary>
    /// Recupera o artifact de uma tentativa arquivada e valida o checksum do patch contra o
    /// manifest. Qualquer inconsistência é um código de recusa tipado — nunca um best effort
    /// silencioso que confie num patch adulterado ou ausente.
    /// </summary>
    public ArchiveLoadResult TryLoad(string attemptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        var manifestPath = ManifestPath(attemptId);
        if (!File.Exists(manifestPath))
        {
            return ArchiveLoadResult.Fail("archive.manifest_missing");
        }

        ArchivedAttemptManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ArchivedAttemptManifest>(
                File.ReadAllText(manifestPath), Json);
        }
        catch (JsonException)
        {
            return ArchiveLoadResult.Fail("archive.manifest_invalid");
        }

        if (manifest is null ||
            string.IsNullOrWhiteSpace(manifest.PatchFileName) ||
            string.IsNullOrWhiteSpace(manifest.PatchSha256) ||
            !string.Equals(manifest.AttemptId, attemptId, StringComparison.Ordinal))
        {
            return ArchiveLoadResult.Fail("archive.manifest_invalid");
        }

        if (manifest.SchemaVersion != 1)
        {
            return ArchiveLoadResult.Fail("archive.schema_unsupported");
        }

        // O nome do patch é resolvido DENTRO da raiz do arquivo: nunca um path absoluto ou
        // que escape o diretório governado.
        var patchPath = Path.GetFullPath(Path.Combine(_root, manifest.PatchFileName));
        if (!patchPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.GetFileName(patchPath) != manifest.PatchFileName)
        {
            return ArchiveLoadResult.Fail("archive.patch_path_invalid");
        }

        if (!File.Exists(patchPath))
        {
            return ArchiveLoadResult.Fail("archive.patch_missing");
        }

        var content = File.ReadAllText(patchPath);
        var actual = Sha256Hex(content);
        if (!string.Equals(actual, manifest.PatchSha256, StringComparison.OrdinalIgnoreCase))
        {
            return ArchiveLoadResult.Fail("archive.checksum_mismatch");
        }

        return new ArchiveLoadResult(true, "archive.loaded", manifest, patchPath, content);
    }

    /// <summary>
    /// Arquiva um patch reprovado com seu manifest de provenance. Escreve o patch primeiro e
    /// o manifest por último, de modo que um manifest presente sempre aponta para um patch
    /// já gravado — nunca o contrário.
    /// </summary>
    public ArchivedAttemptManifest Write(ArchivedAttemptManifest manifest, string patchContent)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(patchContent);
        Directory.CreateDirectory(_root);

        var sealedManifest = manifest with { PatchSha256 = Sha256Hex(patchContent) };
        var patchPath = Path.GetFullPath(Path.Combine(_root, sealedManifest.PatchFileName));
        if (!patchPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The patch file must live inside the archive root.", nameof(manifest));
        }

        File.WriteAllText(patchPath, patchContent);
        File.WriteAllText(ManifestPath(sealedManifest.AttemptId), JsonSerializer.Serialize(sealedManifest, Json));
        return sealedManifest;
    }

    public static string Sha256Hex(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
