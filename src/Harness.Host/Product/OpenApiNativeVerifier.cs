using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Modules.Workflows.Product;

namespace Harness.Host.Product;

/// <summary>
/// Prova que a entrega PUBLICA um contrato OpenAPI de verdade — subindo a aplicação e buscando o
/// documento nela.
///
/// Por que isto precisa existir: enquanto o requisito era satisfeito por um script do manifesto, um
/// <c>"openapi": "node -e \"process.exit(0)\""</c> devolvia exit zero e o portão registrava
/// "contrato gerado". O script roda sob o comando do Poseidon, mas o que ele faz foi escrito por
/// quem está sendo avaliado. Aqui não há script: o Poseidon compila, sobe, busca, faz parse e
/// guarda o hash do documento que recebeu.
/// </summary>
public sealed class OpenApiNativeVerifier(TrustedProcessRunner runner) : ProcessProductVerifier(runner)
{
    /// <summary>Teto de subida. Aplicação que não atende em dois minutos não vai atender.</summary>
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(2);

    public override ProductEvidenceKind Kind => ProductEvidenceKind.OpenApiGenerated;

    public override string Name => "openapi-native";

    public override bool AppliesTo(ProjectEffectiveProfile profile) =>
        profile.Api.OpenApiRequired &&
        (profile.Backend.Runtime?.Contains(".NET", StringComparison.OrdinalIgnoreCase) ?? true);

    public override async Task<ProductVerificationRecord?> VerifyAsync(
        ProductVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (project, ambiguity) = WebSurfaceLocator.LocateAspNetCore(
            context.WorkspaceRoot, SafeFind(context.WorkspaceRoot, "*.csproj"));
        if (ambiguity is not null)
        {
            return Negative(context, ambiguity);
        }

        if (project is null)
        {
            // Nenhuma aplicação web na entrega: não há contrato para buscar. Ausência de evidência,
            // que o portão lê como reprovação — nunca um resultado positivo inventado.
            return null;
        }

        var discovery = WebSurfaceLocator.DiscoverOpenApiPaths(context.WorkspaceRoot, project);

        await using var session = await DeliveryApiSession.StartAsync(
            Runner, context.WorkspaceRoot, project, ReadinessTimeout, cancellationToken);
        if (!session.IsUp)
        {
            return Negative(context, session.Failure ?? "A aplicação não subiu.", session.CommandLine);
        }

        var (document, path, failure) = await FetchAsync(
            session.Client!, session.BaseUrl!, discovery.Paths, cancellationToken);
        if (document is null)
        {
            return Negative(
                context,
                $"{failure} Rotas tentadas ({discovery.Source}): {string.Join(", ", discovery.Paths)}.",
                session.CommandLine);
        }

        var inspection = Inspect(document);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)));

        if (!inspection.Valid)
        {
            return Negative(
                context,
                $"O documento em {path} não é um OpenAPI válido: {inspection.Reason} " +
                $"(sha256 {hash[..12]}).",
                session.CommandLine);
        }

        return new ProductVerificationRecord(
            Kind,
            true,
            Name,
            $"GET {path}",
            0,
            context.CommitSha,
            DateTimeOffset.UtcNow,
            context.AttemptId,
            path,
            $"Contrato OpenAPI {inspection.Version} obtido da aplicação em {path} " +
            $"({inspection.EndpointCount} caminhos, {inspection.OperationCount} operações, " +
            $"sha256 {hash[..12]}); rota descoberta por {discovery.Source}.",
            VerificationTrustLevel.PoseidonControlled);
    }

    /// <summary>
    /// Tenta as rotas candidatas na ordem. Uma candidata só serve se devolver algo que FAZ PARSE
    /// como JSON — 404 e página HTML não são contrato, e aceitar qualquer 200 transformaria o
    /// index do SPA em prova de OpenAPI.
    /// </summary>
    private static async Task<(string? Document, string? Path, string? Failure)> FetchAsync(
        HttpClient client,
        string baseUrl,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        var reasons = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                using var response = await client.GetAsync(baseUrl + candidate, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    reasons.Add($"{candidate} → HTTP {(int)response.StatusCode}");
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(body) || !LooksLikeJsonObject(body))
                {
                    reasons.Add($"{candidate} → resposta não é um objeto JSON");
                    continue;
                }

                return (body, candidate, null);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException)
            {
                reasons.Add($"{candidate} → {exception.GetType().Name}");
            }
        }

        return (null, null,
            "A aplicação subiu mas não publicou contrato em nenhuma rota conhecida " +
            $"({string.Join("; ", reasons)}).");
    }

    private static bool LooksLikeJsonObject(string body) => body.TrimStart().StartsWith('{');

    private sealed record OpenApiInspection(
        bool Valid, string? Reason, string Version, int EndpointCount, int OperationCount);

    /// <summary>
    /// Valida o documento como contrato, não como texto: precisa declarar a versão do formato e
    /// precisa ter <c>paths</c>. Um JSON qualquer devolvido com 200 não é OpenAPI.
    /// </summary>
    private static OpenApiInspection Inspect(string document)
    {
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(document);
        }
        catch (JsonException exception)
        {
            return new OpenApiInspection(false, $"o parse falhou ({exception.GetType().Name})", "?", 0, 0);
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new OpenApiInspection(false, "a raiz não é um objeto", "?", 0, 0);
            }

            var version = Version(root);
            if (version is null)
            {
                return new OpenApiInspection(
                    false, "não declara `openapi` nem `swagger`", "?", 0, 0);
            }

            if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
            {
                return new OpenApiInspection(
                    false, "não declara o objeto `paths`", version, 0, 0);
            }

            var endpoints = 0;
            var operations = 0;
            foreach (var path in paths.EnumerateObject())
            {
                endpoints++;
                if (path.Value.ValueKind == JsonValueKind.Object)
                {
                    operations += path.Value.EnumerateObject()
                        .Count(property => IsOperation(property.Name));
                }
            }

            // Contrato sem nenhuma operação é forma sem conteúdo: a aplicação publicou o envelope e
            // não expôs nada. Aprovar isso devolveria a mesma classe de mentira que o script falso.
            return operations == 0
                ? new OpenApiInspection(
                    false, "o contrato não declara nenhuma operação HTTP", version, endpoints, 0)
                : new OpenApiInspection(true, null, version, endpoints, operations);
        }
    }

    private static string? Version(JsonElement root)
    {
        if (root.TryGetProperty("openapi", out var openapi) && openapi.ValueKind == JsonValueKind.String)
        {
            return openapi.GetString();
        }

        return root.TryGetProperty("swagger", out var swagger) && swagger.ValueKind == JsonValueKind.String
            ? "swagger " + swagger.GetString()
            : null;
    }

    internal static bool IsOperation(string name) => name.ToLowerInvariant() is
        "get" or "post" or "put" or "patch" or "delete" or "head" or "options" or "trace";

    private ProductVerificationRecord Negative(
        ProductVerificationContext context, string reason, string? command = null) =>
        new(Kind, false, Name, command ?? "openapi-native", -1, context.CommitSha,
            DateTimeOffset.UtcNow, context.AttemptId, ".", reason,
            VerificationTrustLevel.PoseidonControlled);
}
