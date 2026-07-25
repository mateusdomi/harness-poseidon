using System.Text.Json;

namespace Harness.Host.Leadership;

/// <summary>
/// Personalização local da liderança. O arquivo é deliberadamente externo ao banco do
/// projeto: pertence à instalação/usuário desta máquina e não entra em prompts de execução.
/// </summary>
public sealed class LeadershipProfileStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _profilePath;
    private readonly string _assetDirectory;

    public LeadershipProfileStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".harness-poseidon"))
    {
    }

    public LeadershipProfileStore(string dataDirectory)
    {
        _profilePath = Path.Combine(dataDirectory, "leadership-profile.json");
        _assetDirectory = Path.Combine(dataDirectory, "assets");
    }

    public async Task<LeadershipProfileRecord> ReadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            return await ReadUnsafeAsync(token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LeadershipProfileRecord> UpdateAsync(
        LeadershipProfileWriteRequest request,
        string actorProfileId,
        DateTimeOffset now,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        await _gate.WaitAsync(token);
        try
        {
            var current = await ReadUnsafeAsync(token);
            if (request.ExpectedVersion != current.Version)
                throw new LeadershipProfileConflictException();

            var changes = DescribeChanges(current, request);
            var next = new LeadershipProfileRecord(
                Clean(request.DisplayName),
                Clean(request.Title),
                current.PhotoUrl,
                Clean(request.Summary),
                CleanList(request.Specialties),
                Clean(request.CareerSummary),
                CleanList(request.Languages),
                Clean(request.Personality),
                CleanList(request.Hobbies),
                request.Age,
                Clean(request.CommunicationInstructions),
                Blank(request.PreferredModelId),
                Blank(request.PreferredAccountId),
                current.Version + 1,
                now,
                current.History
                    .Append(new LeadershipProfileRevision(
                        current.Version + 1, actorProfileId, now, changes))
                    .TakeLast(100)
                    .ToArray());
            await WriteUnsafeAsync(next, token);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LeadershipProfileRecord> SavePhotoAsync(
        Stream content,
        string extension,
        string actorProfileId,
        DateTimeOffset now,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(_assetDirectory);
            var path = Path.Combine(_assetDirectory, $"bruna-magalhaes{extension}");
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                await content.CopyToAsync(output, token);
                await output.FlushAsync(token);
            }
            File.Move(temporary, path, overwrite: true);
            RemoveAlternativePhotos("bruna-magalhaes", extension);

            var current = await ReadUnsafeAsync(token);
            var next = current with
            {
                PhotoUrl = "/api/v1/leadership-profile/photo",
                Version = current.Version + 1,
                UpdatedAt = now,
                History = current.History
                    .Append(new LeadershipProfileRevision(
                        current.Version + 1, actorProfileId, now, ["Foto atualizada."]))
                    .TakeLast(100)
                    .ToArray(),
            };
            await WriteUnsafeAsync(next, token);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? ResolvePhotoPath()
    {
        foreach (var extension in new[] { ".jpg", ".png", ".webp" })
        {
            var path = Path.Combine(_assetDirectory, $"bruna-magalhaes{extension}");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public async Task SaveAgentPhotoAsync(
        string alias,
        Stream content,
        string extension,
        CancellationToken token = default)
    {
        var safeAlias = ValidateAlias(alias);
        await _gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(_assetDirectory);
            var stem = $"agent-{safeAlias}";
            var path = Path.Combine(_assetDirectory, $"{stem}{extension}");
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                await content.CopyToAsync(output, token);
                await output.FlushAsync(token);
            }
            File.Move(temporary, path, overwrite: true);
            RemoveAlternativePhotos(stem, extension);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? ResolveAgentPhotoPath(string alias)
    {
        var safeAlias = ValidateAlias(alias);
        foreach (var extension in new[] { ".jpg", ".png", ".webp" })
        {
            var path = Path.Combine(_assetDirectory, $"agent-{safeAlias}{extension}");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public void Dispose() => _gate.Dispose();

    private async Task<LeadershipProfileRecord> ReadUnsafeAsync(CancellationToken token)
    {
        if (!File.Exists(_profilePath)) return LeadershipProfileRecord.Default;
        try
        {
            await using var stream = new FileStream(
                _profilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            return await JsonSerializer.DeserializeAsync<LeadershipProfileRecord>(
                       stream, JsonOptions, token)
                   ?? LeadershipProfileRecord.Default;
        }
        catch (JsonException)
        {
            throw new LeadershipProfileInvalidException();
        }
    }

    private async Task WriteUnsafeAsync(LeadershipProfileRecord value, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(_profilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = $"{_profilePath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(
            temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token);
            await stream.FlushAsync(token);
        }
        File.Move(temporary, _profilePath, overwrite: true);
    }

    private static void Validate(LeadershipProfileWriteRequest value)
    {
        if (string.IsNullOrWhiteSpace(value.DisplayName) || value.DisplayName.Trim().Length > 120)
            throw new LeadershipProfileValidationException("O nome exibido deve ter entre 1 e 120 caracteres.");
        if (string.IsNullOrWhiteSpace(value.Title) || value.Title.Trim().Length > 200)
            throw new LeadershipProfileValidationException("O cargo deve ter entre 1 e 200 caracteres.");
        if (value.Age is < 18 or > 120)
            throw new LeadershipProfileValidationException("A idade deve estar entre 18 e 120 anos.");
        if (value.Summary.Length > 4000 || value.CareerSummary.Length > 4000 ||
            value.Personality.Length > 2000 || value.CommunicationInstructions.Length > 4000)
            throw new LeadershipProfileValidationException("Um ou mais campos excedem o limite permitido.");
        if (value.Specialties.Count > 30 || value.Languages.Count > 20 || value.Hobbies.Count > 30)
            throw new LeadershipProfileValidationException("Uma ou mais listas excedem o limite permitido.");
    }

    private static List<string> DescribeChanges(
        LeadershipProfileRecord current,
        LeadershipProfileWriteRequest next)
    {
        var changes = new List<string>();
        Add(current.DisplayName, next.DisplayName, "Nome exibido");
        Add(current.Title, next.Title, "Cargo");
        Add(current.CommunicationInstructions, next.CommunicationInstructions, "Camada de comunicação");
        Add(current.PreferredModelId, next.PreferredModelId, "Modelo padrão");
        Add(current.PreferredAccountId, next.PreferredAccountId, "Conta preferencial");
        if (changes.Count == 0) changes.Add("Perfil humanizado atualizado.");
        return changes;

        void Add(string? before, string? after, string label)
        {
            if (!string.Equals(Blank(before), Blank(after), StringComparison.Ordinal))
                changes.Add($"{label}: {Display(before)} → {Display(after)}");
        }
    }

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "não configurado" : value.Trim();
    private static string Clean(string value) => value.Trim();
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string[] CleanList(IReadOnlyList<string> values) =>
        values.Select(Clean).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private void RemoveAlternativePhotos(string stem, string preservedExtension)
    {
        foreach (var extension in new[] { ".jpg", ".png", ".webp" })
        {
            if (extension == preservedExtension) continue;
            var path = Path.Combine(_assetDirectory, $"{stem}{extension}");
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string ValidateAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias) || alias.Length > 100 ||
            alias.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new LeadershipProfileValidationException("A identidade do agente é inválida.");
        return alias;
    }
}

public sealed record LeadershipProfileRecord(
    string DisplayName,
    string Title,
    string PhotoUrl,
    string Summary,
    IReadOnlyList<string> Specialties,
    string CareerSummary,
    IReadOnlyList<string> Languages,
    string Personality,
    IReadOnlyList<string> Hobbies,
    int Age,
    string CommunicationInstructions,
    string? PreferredModelId,
    string? PreferredAccountId,
    int Version,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<LeadershipProfileRevision> History)
{
    public static LeadershipProfileRecord Default { get; } = new(
        "Bruna Magalhães",
        "Diretora de Engenharia e Operações de IA",
        "/people/bruna-magalhaes.jpg",
        "Liderança técnica orientada a entregas seguras, rastreáveis e úteis para o negócio.",
        ["Engenharia de software", "Operações de IA", "Governança", "Gestão de entregas"],
        "Experiência em coordenação de equipes multidisciplinares, arquitetura e operação de produtos de IA.",
        ["Português (Brasil)", "Inglês"],
        "Pragmática, transparente, cuidadosa com riscos e direta nas decisões.",
        ["Café", "Leitura", "Tecnologia", "Caminhadas"],
        27,
        "Chame o usuário pelo nome quando conhecido. Use tom profissional, leve e direto, em português do Brasil.",
        null,
        null,
        1,
        DateTimeOffset.UnixEpoch,
        []);
}

public sealed record LeadershipProfileRevision(
    int Version,
    string ChangedBy,
    DateTimeOffset ChangedAt,
    IReadOnlyList<string> Changes);

public sealed record LeadershipProfileWriteRequest(
    string DisplayName,
    string Title,
    string Summary,
    IReadOnlyList<string> Specialties,
    string CareerSummary,
    IReadOnlyList<string> Languages,
    string Personality,
    IReadOnlyList<string> Hobbies,
    int Age,
    string CommunicationInstructions,
    string? PreferredModelId,
    string? PreferredAccountId,
    int ExpectedVersion);

public sealed class LeadershipProfileValidationException(string message) : Exception(message);
public sealed class LeadershipProfileConflictException : Exception;
public sealed class LeadershipProfileInvalidException : Exception;
