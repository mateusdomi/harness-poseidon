using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Modules.Workflows.Product;

/// <summary>
/// Contrato DECLARADO de como provar um produto pelo navegador — o manifesto
/// <c>.harness/e2e.json</c> na raiz do repositório entregue.
///
/// Existe porque a auto-verificação do ATOR sobre E2E provou-se não confiável (2026-08-08: um
/// objetivo foi submetido como "E2E verde" com 22 falhas reais; o sandbox do executor nem sobe
/// Oracle). A prova de navegador tem que ser executada pela PLATAFORMA, de forma independente —
/// e cada produto varia (diretório do E2E, nomes de variável, banco), então o produto declara
/// como subir e rodar, em vez de a plataforma adivinhar.
///
/// PURO: só o formato. A execução (compose, boot, playwright, teardown) vive no host.
/// </summary>
public sealed record ProductE2EHarness(
    [property: JsonPropertyName("composeFile")] string? ComposeFile,
    [property: JsonPropertyName("composeService")] string? ComposeService,
    [property: JsonPropertyName("apiProject")] string? ApiProject,
    [property: JsonPropertyName("apiHealthPath")] string ApiHealthPath,
    [property: JsonPropertyName("apiUrl")] string ApiUrl,
    [property: JsonPropertyName("frontUrl")] string? FrontUrl,
    [property: JsonPropertyName("e2eDir")] string E2eDir,
    [property: JsonPropertyName("e2eCommand")] IReadOnlyList<string> E2eCommand,
    [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string> Env)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public const string ManifestPath = ".harness/e2e.json";

    /// <summary>
    /// Lê o manifesto, ou <see langword="null"/> quando ausente/ilegível. Ausência NÃO é erro do
    /// parser: é fato a reportar — um produto com interface e sem manifesto E2E não pode se
    /// provar, e isso é reprovação do gate, não exceção aqui.
    /// </summary>
    public static ProductE2EHarness? TryLoad(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var path = Path.Combine(repositoryRoot, ManifestPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var harness = JsonSerializer.Deserialize<ProductE2EHarness>(
                File.ReadAllText(path), Options);
            return harness is { E2eDir.Length: > 0, E2eCommand.Count: > 0 } ? harness : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
