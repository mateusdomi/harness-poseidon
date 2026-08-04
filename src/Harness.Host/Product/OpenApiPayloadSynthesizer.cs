using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harness.Host.Product;

/// <summary>
/// Constrói um corpo de requisição a partir do SCHEMA declarado no contrato da entrega.
///
/// A regra do §14 que este tipo existe para cumprir: <b>o Poseidon não pode assumir nomes de tabela
/// nem domínio.</b> Ele não sabe o que é um empréstimo, um paciente ou uma apólice — e não precisa.
/// O que ele sabe é o que a própria aplicação declarou aceitar. O dado sintetizado carrega uma
/// marca reconhecível para que a leitura posterior possa identificá-lo sem depender de campo de
/// negócio nenhum.
/// </summary>
public static class OpenApiPayloadSynthesizer
{
    /// <summary>Marca do dado escrito pela verificação. Aparece em todo campo textual sintetizado.</summary>
    public const string Marker = "poseidon-persistence-probe";

    private const int MaxDepth = 6;

    /// <summary>
    /// Sintetiza um corpo válido, ou nulo quando o schema exige algo que não dá para inventar com
    /// segurança. Nulo aqui vira <c>NotSupported</c> lá em cima — nunca uma tentativa às cegas.
    /// </summary>
    public static JsonNode? Synthesize(JsonElement schema, JsonElement components, string discriminator) =>
        Build(schema, components, discriminator, 0, []);

    private static JsonNode? Build(
        JsonElement schema,
        JsonElement components,
        string discriminator,
        int depth,
        HashSet<string> visitedRefs)
    {
        if (depth > MaxDepth || schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (schema.TryGetProperty("$ref", out var reference) &&
            reference.ValueKind == JsonValueKind.String)
        {
            var name = reference.GetString()!;

            // Schema que se refere a si mesmo produz recursão infinita; parar aqui e devolver nulo
            // é melhor do que gerar um corpo arbitrariamente profundo que a aplicação vai recusar.
            if (!visitedRefs.Add(name))
            {
                return null;
            }

            var resolved = Resolve(name, components);
            return resolved is null ? null : Build(resolved.Value, components, discriminator, depth + 1, visitedRefs);
        }

        foreach (var composite in (string[])["allOf", "oneOf", "anyOf"])
        {
            if (schema.TryGetProperty(composite, out var options) &&
                options.ValueKind == JsonValueKind.Array &&
                options.GetArrayLength() > 0)
            {
                return Build(options[0], components, discriminator, depth + 1, visitedRefs);
            }
        }

        if (schema.TryGetProperty("enum", out var enumeration) &&
            enumeration.ValueKind == JsonValueKind.Array &&
            enumeration.GetArrayLength() > 0)
        {
            return JsonNode.Parse(enumeration[0].GetRawText());
        }

        var type = schema.TryGetProperty("type", out var declared) && declared.ValueKind == JsonValueKind.String
            ? declared.GetString()
            : schema.TryGetProperty("properties", out _) ? "object" : null;

        return type switch
        {
            "object" => BuildObject(schema, components, discriminator, depth, visitedRefs),
            "array" => BuildArray(schema, components, discriminator, depth, visitedRefs),
            "string" => JsonValue.Create(BuildString(schema, discriminator)),
            "integer" => JsonValue.Create(7),
            "number" => JsonValue.Create(7.5),
            "boolean" => JsonValue.Create(true),
            _ => null,
        };
    }

    private static JsonObject? BuildObject(
        JsonElement schema,
        JsonElement components,
        string discriminator,
        int depth,
        HashSet<string> visitedRefs)
    {
        if (!schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return new JsonObject();
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredList) &&
            requiredList.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredList.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    required.Add(item.GetString()!);
                }
            }
        }

        var result = new JsonObject();
        foreach (var property in properties.EnumerateObject())
        {
            // O identificador é da aplicação: mandar um `id` inventado no POST faz servidores sérios
            // recusarem o corpo, e a verificação reprovaria por culpa do instrumento.
            if (IsReadOnly(property.Value) || IsIdentifier(property.Name))
            {
                continue;
            }

            var value = Build(property.Value, components, discriminator, depth + 1, [.. visitedRefs]);
            if (value is null)
            {
                // Campo obrigatório que não dá para sintetizar inviabiliza o corpo inteiro.
                if (required.Contains(property.Name))
                {
                    return null;
                }

                continue;
            }

            result[property.Name] = value;
        }

        return result.Count == 0 ? null : result;
    }

    private static JsonArray? BuildArray(
        JsonElement schema,
        JsonElement components,
        string discriminator,
        int depth,
        HashSet<string> visitedRefs)
    {
        if (!schema.TryGetProperty("items", out var items))
        {
            return new JsonArray();
        }

        var element = Build(items, components, discriminator, depth + 1, visitedRefs);
        return element is null ? new JsonArray() : new JsonArray(element);
    }

    /// <summary>
    /// Texto que respeita o formato declarado. O discriminador entra no valor livre para que a
    /// leitura posterior reconheça o registro sem saber nada do domínio.
    /// </summary>
    private static string BuildString(JsonElement schema, string discriminator)
    {
        var format = schema.TryGetProperty("format", out var declared) &&
            declared.ValueKind == JsonValueKind.String
            ? declared.GetString()
            : null;

        return format switch
        {
            "date-time" => "2026-01-01T12:00:00Z",
            "date" => "2026-01-01",
            "time" => "12:00:00",
            "uuid" => "00000000-0000-4000-8000-000000000001",
            "email" => $"{discriminator}@example.invalid",
            "uri" or "url" => $"https://example.invalid/{discriminator}",
            _ => Fit(schema, $"{Marker}-{discriminator}"),
        };
    }

    /// <summary>Respeita <c>maxLength</c>: exceder o limite declarado reprovaria por validação, não por persistência.</summary>
    private static string Fit(JsonElement schema, string value)
    {
        if (schema.TryGetProperty("maxLength", out var max) && max.TryGetInt32(out var limit) &&
            limit > 0 && value.Length > limit)
        {
            return value[..limit];
        }

        if (schema.TryGetProperty("minLength", out var min) && min.TryGetInt32(out var floor) &&
            value.Length < floor)
        {
            return value.PadRight(floor, 'x');
        }

        return value;
    }

    private static bool IsReadOnly(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object &&
        schema.TryGetProperty("readOnly", out var readOnly) &&
        readOnly.ValueKind == JsonValueKind.True;

    private static bool IsIdentifier(string name) =>
        string.Equals(name, "id", StringComparison.OrdinalIgnoreCase);

    private static JsonElement? Resolve(string reference, JsonElement components)
    {
        const string prefix = "#/components/schemas/";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal) ||
            components.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return components.TryGetProperty(reference[prefix.Length..], out var schema) ? schema : null;
    }

    /// <summary>Discriminador estável e único por execução, sem depender de relógio nem de sorte.</summary>
    public static string Discriminator(string commitSha, string kind) =>
        $"{kind}-{(commitSha.Length >= 8 ? commitSha[..8] : commitSha)}-" +
        $"{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}";
}
