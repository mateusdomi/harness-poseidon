using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Modules.Documents.Application;

/// <summary>
/// Monta o pacote de documentos que o dono leva para fora do Poseidon.
///
/// A regra que orienta tudo aqui é: o pacote é uma FOTOGRAFIA HONESTA do que a
/// equipe entregou e o dono aprovou. Por isso só entra documento no estado
/// aprovado — rascunho e material em revisão ainda podem mudar, e mandar isso
/// para um sócio ou cliente como se fosse entrega é mentir por omissão de
/// contexto. Documento marcado como inconsistente também fica de fora: o
/// próprio sistema já diz que não confia nele.
///
/// O manifesto acompanha o pacote porque, meses depois, a pergunta que aparece
/// é "qual versão disto foi parar na mão do cliente?" — e a resposta precisa
/// estar dentro do próprio pacote, não na memória de alguém.
/// </summary>
public static class DocumentExportComposer
{
    /// <summary>Estado em que um documento é entregável para fora.</summary>
    public const string ApprovedState = "approved";

    /// <summary>Nome do manifesto dentro do pacote.</summary>
    public const string ManifestEntryPath = "manifesto.json";

    /// <summary>Pasta usada quando o documento não pertence a nenhuma etapa.</summary>
    public const string PhaselessFolder = "sem-etapa";

    /// <summary>
    /// Só o que é entregável: aprovado e sem marca de inconsistência. A ordem é
    /// determinística (etapa, título, id) para que o mesmo projeto produza
    /// sempre o mesmo pacote — dois downloads iguais não podem divergir.
    /// </summary>
    public static IReadOnlyList<ExportableDocument> Exportable(
        IEnumerable<ExportableDocument> documents) =>
        [.. documents
            .Where(document =>
                string.Equals(document.State, ApprovedState, StringComparison.Ordinal)
                && !document.Inconsistent)
            .OrderBy(document => document.PhaseName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(document => document.Title, StringComparer.Ordinal)
            .ThenBy(document => document.Id, StringComparer.Ordinal)];

    /// <summary>
    /// Caminho do documento dentro do pacote: pasta da etapa e nome legível com
    /// a versão. O nome vem do título, não do identificador, porque quem abre o
    /// ZIP é uma pessoa — mas o identificador entra no manifesto, para que a
    /// rastreabilidade não dependa do nome.
    /// </summary>
    public static string EntryPath(ExportableDocument document)
    {
        var folder = Slug(document.PhaseName) is { Length: > 0 } phase ? phase : PhaselessFolder;
        var name = Slug(document.Title) is { Length: > 0 } title ? title : "documento";
        var version = document.CurrentVersion.ToString(CultureInfo.InvariantCulture);
        return $"{folder}/{name}-v{version}.md";
    }

    /// <summary>
    /// Monta o pacote. Títulos repetidos não se sobrescrevem: o segundo ganha um
    /// sufixo com o fim do identificador, porque perder um documento no ZIP é
    /// pior do que um nome feio.
    /// </summary>
    public static DocumentExportPlan Compose(
        string projectId,
        IEnumerable<DocumentExportSource> sources,
        DateTimeOffset generatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var entries = new List<DocumentExportEntry>();
        var manifestItems = new List<DocumentExportManifestItem>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var path = EntryPath(source.Document);
            if (!taken.Add(path))
            {
                var suffix = source.Document.Id[^Math.Min(6, source.Document.Id.Length)..];
                path = path[..^3] + $"-{suffix}.md";
                taken.Add(path);
            }

            entries.Add(new DocumentExportEntry(path, source.Body));
            manifestItems.Add(new DocumentExportManifestItem(
                source.Document.Id,
                source.Document.Title,
                source.Document.Kind,
                source.Document.PhaseName,
                source.Document.CurrentVersion,
                source.ContentHash,
                source.Document.UpdatedAt,
                path));
        }

        var manifest = new DocumentExportManifest(
            projectId, generatedAt, manifestItems.Count, manifestItems);
        var manifestJson = JsonSerializer.Serialize(manifest, ManifestOptions);
        entries.Insert(0, new DocumentExportEntry(ManifestEntryPath, manifestJson));

        return new DocumentExportPlan(entries, manifestJson, manifestItems.Count);
    }

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        // camelCase e acento preservado: o manifesto segue o mesmo padrão dos
        // contratos do produto e é lido por gente, não só por máquina.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Nome de arquivo seguro e legível: sem acento, sem separador de caminho e
    /// sem espaço. Não é enfeite — título com barra criaria pasta dentro do ZIP
    /// e título com acento quebra em sistemas de arquivos alheios.
    /// </summary>
    public static string Slug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        return builder.ToString().Trim('-');
    }
}

/// <summary>
/// O documento como a exportação precisa dele. É um tipo próprio do módulo e
/// não o registro de persistência: quem monta o pacote não conhece o banco.
/// </summary>
public sealed record ExportableDocument(
    string Id,
    string Title,
    string Kind,
    string State,
    int CurrentVersion,
    string? PhaseName,
    bool Inconsistent,
    DateTimeOffset UpdatedAt);

/// <summary>Documento aprovado mais o corpo lido do catálogo de conteúdo.</summary>
public sealed record DocumentExportSource(
    ExportableDocument Document, string Body, string ContentHash);

/// <summary>Um arquivo dentro do pacote.</summary>
public sealed record DocumentExportEntry(string Path, string Body);

/// <summary>Pacote pronto para ser escrito como ZIP.</summary>
public sealed record DocumentExportPlan(
    IReadOnlyList<DocumentExportEntry> Entries, string ManifestJson, int DocumentCount);

public sealed record DocumentExportManifest(
    string ProjectId,
    DateTimeOffset GeneratedAt,
    int DocumentCount,
    IReadOnlyList<DocumentExportManifestItem> Documents);

public sealed record DocumentExportManifestItem(
    string DocumentId,
    string Title,
    string Kind,
    string? PhaseName,
    int Version,
    string ContentHash,
    DateTimeOffset UpdatedAt,
    string Path);
