using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harness.Host.Product;

/// <summary>A rota escolhida para provar persistência e o corpo derivado do schema dela.</summary>
public sealed record ResourceTarget(string Path, JsonNode Payload);

/// <summary>
/// Escolhe, DENTRO DO CONTRATO que a aplicação publicou, um recurso que aceite escrita e leitura na
/// mesma rota — e sintetiza o corpo a partir do schema declarado.
///
/// A escolha é determinística e explicável: rotas sem parâmetro de caminho, que declarem
/// <c>POST</c> e <c>GET</c>, ordenadas pelo caminho. Nada aqui conhece domínio; o critério é
/// estrutural, e é o que permite provar persistência num sistema de empréstimos e num de agenda
/// médica com o mesmo código.
/// </summary>
public static class ResourceTargetResolver
{
    public static ResourceTarget? Resolve(string contract, string discriminator)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(contract);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var components = root.TryGetProperty("components", out var element) &&
                element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("schemas", out var schemas)
                ? schemas
                : default;

            foreach (var path in paths.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                // Rota com parâmetro de caminho não é coleção: postar em `/itens/{id}` exigiria
                // inventar o id, e inventar identificador é o oposto de derivar do contrato.
                if (path.Name.Contains('{', StringComparison.Ordinal) ||
                    path.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!path.Value.TryGetProperty("post", out var post) ||
                    !path.Value.TryGetProperty("get", out _))
                {
                    continue;
                }

                var schema = RequestSchema(post);
                if (schema is null)
                {
                    continue;
                }

                var payload = OpenApiPayloadSynthesizer.Synthesize(
                    schema.Value, components, discriminator);
                if (payload is not null)
                {
                    return new ResourceTarget(path.Name, payload);
                }
            }

            return null;
        }
    }

    /// <summary>O schema do corpo JSON que a operação declara aceitar.</summary>
    private static JsonElement? RequestSchema(JsonElement operation)
    {
        if (!operation.TryGetProperty("requestBody", out var body) ||
            body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var media in content.EnumerateObject())
        {
            if (!media.Name.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (media.Value.ValueKind == JsonValueKind.Object &&
                media.Value.TryGetProperty("schema", out var schema))
            {
                return schema;
            }
        }

        return null;
    }
}
