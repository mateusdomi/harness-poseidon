using System.Globalization;
using System.IO.Compression;
using System.Text;
using Harness.Host.Profiles;
using Harness.Modules.Documents.Application;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Documents;

/// <summary>
/// Exportação dos documentos do projeto (F5): o dono leva a produção da equipe
/// para fora — sócio, cliente, contador, auditoria — em um pacote único.
///
/// Só sai o que está aprovado, cada arquivo carrega a versão no nome e o
/// manifesto responde depois "qual versão foi parar na mão de quem". A saída é
/// registrada: exportar é o momento em que conteúdo interno vira externo, e
/// isso é um fato que merece rastro.
/// </summary>
public static class DocumentExportEndpoints
{
    /// <summary>Teto de documentos por pacote: o dono leva o projeto, não o histórico do mundo.</summary>
    private const int MaxDocuments = 500;

    public static IEndpointRouteBuilder MapDocumentExport(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/projects/{projectId}/documents/export", ExportAsync)
            .WithTags("documents")
            .Produces(200, contentType: "application/zip")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> ExportAsync(
        string projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IDocumentCatalogStore catalog,
        IDocumentContentCatalog content,
        IDocumentExportStore exports,
        IClock clock,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _))
        {
            return Results.Problem(statusCode: 400, title: "invalid_id", detail: "ID must be a ULID.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Results.Problem(
                statusCode: 401, title: "local_session_required",
                detail: "A local profile session is required.");
        }

        var project = await projects.GetAsync(profile.TenantId, projectId, token);
        if (project is null)
        {
            return Results.Problem(
                statusCode: 404, title: "project_not_found", detail: "The resource does not exist.");
        }

        var documents = await catalog.ListDocumentsAsync(
            profile.TenantId, projectId, afterId: null, MaxDocuments, token);
        var exportable = DocumentExportComposer.Exportable(documents.Select(Map));

        var sources = new List<DocumentExportSource>(exportable.Count);
        foreach (var document in exportable)
        {
            var versions = await catalog.ListVersionsAsync(
                profile.TenantId, document.Id, afterId: null, limit: MaxDocuments, token);
            var current = versions
                .Where(version => version.Version == document.CurrentVersion)
                .MaxBy(version => version.CreatedAt);
            if (current is null) continue;

            // Conteúdo ilegível não vira arquivo vazio dentro do pacote: o
            // documento fica de fora e o manifesto não mente sobre ele.
            string body;
            try
            {
                body = await content.ReadAsync(current.CatalogPath, current.ContentHash, token);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                continue;
            }

            sources.Add(new DocumentExportSource(document, body, current.ContentHash));
        }

        var generatedAt = clock.UtcNow;
        var plan = DocumentExportComposer.Compose(projectId, sources, generatedAt);
        var archive = Package(plan);

        await exports.RecordAsync(
            new DocumentExportRecordCommand(
                profile.TenantId,
                UlidValue.New(generatedAt).ToString(),
                projectId,
                plan.DocumentCount,
                profile.Id,
                plan.ManifestJson,
                generatedAt),
            token);

        var stamp = generatedAt.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var name = DocumentExportComposer.Slug(project.Key) is { Length: > 0 } key ? key : "projeto";
        return Results.File(archive, "application/zip", $"documentos-{name}-{stamp}.zip");
    }

    private static ExportableDocument Map(DocumentCatalogRecord record) =>
        new(record.Id, record.Title, record.Kind, record.State, record.CurrentVersion,
            record.PhaseName, record.Inconsistent, record.UpdatedAt);

    /// <summary>
    /// Escreve o pacote em memória. O teto de documentos mantém o tamanho no
    /// domínio do razoável, e o modo sem compressão adaptativa evita surpresa
    /// de CPU em máquina de cliente.
    /// </summary>
    private static byte[] Package(DocumentExportPlan plan)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in plan.Entries)
            {
                var file = archive.CreateEntry(entry.Path, CompressionLevel.Optimal);
                using var stream = file.Open();
                var bytes = new UTF8Encoding(false).GetBytes(entry.Body);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        return buffer.ToArray();
    }
}
