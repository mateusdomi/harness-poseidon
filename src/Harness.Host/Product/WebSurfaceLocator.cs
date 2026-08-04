using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harness.Host.Product;

/// <summary>Onde o documento OpenAPI foi procurado, e por quê.</summary>
/// <param name="Paths">Rotas candidatas, na ordem em que serão tentadas.</param>
/// <param name="Source">Como foram descobertas: `metadata`, `code`, `convention`.</param>
public sealed record OpenApiDiscovery(IReadOnlyList<string> Paths, string Source);

/// <summary>
/// Descobre a superfície HTTP da entrega e onde ela publica o contrato.
///
/// Duas regras do §4 moram aqui:
///
/// 1. <b>Nada de sortear.</b> Duas aplicações web na entrega sem forma segura de escolher devolve
///    ambiguidade declarada, não a primeira da lista ordenada — evidência sobre a aplicação errada
///    é pior do que evidência ausente, porque parece prova.
/// 2. <b><c>/swagger/v1/swagger.json</c> é convenção, não lei.</b> O que a entrega DECLARA vence:
///    metadado do produto, depois a rota que o próprio código registra, e só então a convenção.
/// </summary>
public static partial class WebSurfaceLocator
{
    /// <summary>Motivo estável para superfície indecidível. Vira diagnóstico e evidência negativa.</summary>
    public const string Ambiguous = "verification_ambiguous";

    /// <summary>
    /// O projeto ASP.NET Core da entrega, relativo à raiz. Reconhece pelo SDK declarado no csproj —
    /// é o que separa a aplicação web de bibliotecas e projetos de teste sem depender de nome.
    /// </summary>
    public static (string? Project, string? Ambiguity) LocateAspNetCore(
        string workspaceRoot, IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var web = new List<string>();
        foreach (var relative in candidates)
        {
            var absolute = Path.Combine(
                workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            string content;
            try
            {
                content = File.ReadAllText(absolute);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (content.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
            {
                web.Add(relative);
            }
        }

        return web.Count switch
        {
            1 => (web[0], null),
            0 => (null, null),
            _ => (null,
                $"{Ambiguous}: {web.Count} aplicações ASP.NET Core na entrega " +
                $"({string.Join(", ", web.Take(3))}…). Escolher uma no sorteio produziria prova " +
                "sobre outra aplicação."),
        };
    }

    /// <summary>
    /// Onde procurar o contrato. O que a entrega declara vence a convenção; convenção só entra
    /// quando ninguém declarou nada.
    /// </summary>
    public static OpenApiDiscovery DiscoverOpenApiPaths(string workspaceRoot, string projectRelativePath)
    {
        var declared = FromProductMetadata(workspaceRoot);
        if (declared.Count > 0)
        {
            return new OpenApiDiscovery(declared, "metadata");
        }

        var fromCode = FromSourceRegistration(workspaceRoot, projectRelativePath);
        if (fromCode.Count > 0)
        {
            return new OpenApiDiscovery(fromCode, "code");
        }

        // As duas convenções que o ecossistema .NET realmente usa: a nativa (`AddOpenApi`) e a do
        // Swashbuckle. Tentar as duas não é adivinhar — cada candidata só vale se devolver um
        // documento que faz parse como OpenAPI.
        return new OpenApiDiscovery(["/openapi/v1.json", "/swagger/v1/swagger.json"], "convention");
    }

    /// <summary>
    /// Metadado do produto: <c>.poseidon/product.json</c> com <c>{"openApi":{"path":"/…"}}</c>.
    /// É o canal por onde uma entrega que publica o contrato num lugar próprio se declara, em vez
    /// de depender de o Poseidon adivinhar.
    /// </summary>
    private static List<string> FromProductMetadata(string workspaceRoot)
    {
        foreach (var candidate in (string[])[".poseidon/product.json", "poseidon.json"])
        {
            var absolute = Path.Combine(
                workspaceRoot, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(absolute));
                if (document.RootElement.TryGetProperty("openApi", out var openApi) &&
                    openApi.ValueKind == JsonValueKind.Object &&
                    openApi.TryGetProperty("path", out var path) &&
                    path.ValueKind == JsonValueKind.String &&
                    path.GetString() is { Length: > 0 } value)
                {
                    return [Normalize(value)];
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // Metadado ilegível é metadado ausente: cai para a descoberta seguinte.
            }
        }

        return [];
    }

    /// <summary>A rota que o próprio código registra. Mais confiável que convenção e mais barato que subir para descobrir.</summary>
    private static List<string> FromSourceRegistration(string workspaceRoot, string projectRelativePath)
    {
        var projectDirectory = projectRelativePath.Contains('/', StringComparison.Ordinal)
            ? projectRelativePath[..projectRelativePath.LastIndexOf('/')]
            : ".";
        var folder = projectDirectory == "."
            ? workspaceRoot
            : Path.Combine(workspaceRoot, projectDirectory.Replace('/', Path.DirectorySeparatorChar));

        var found = new List<string>();
        foreach (var file in (string[])["Program.cs", "Startup.cs"])
        {
            var absolute = Path.Combine(folder, file);
            if (!File.Exists(absolute))
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(absolute);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (Match match in SwaggerEndpointPattern().Matches(content))
            {
                Add(found, match.Groups[1].Value);
            }

            foreach (Match match in MapOpenApiPattern().Matches(content))
            {
                Add(found, match.Groups[1].Value.Replace("{documentName}", "v1", StringComparison.Ordinal));
            }
        }

        return found;
    }

    private static void Add(List<string> found, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("://", StringComparison.Ordinal))
        {
            return;
        }

        var normalized = Normalize(value);
        if (!found.Contains(normalized, StringComparer.Ordinal))
        {
            found.Add(normalized);
        }
    }

    private static string Normalize(string path) =>
        path.StartsWith('/') ? path : "/" + path;

    [GeneratedRegex(@"SwaggerEndpoint\(\s*""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex SwaggerEndpointPattern();

    [GeneratedRegex(@"MapOpenApi\(\s*""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex MapOpenApiPattern();
}
