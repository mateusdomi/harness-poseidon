using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Modules.Operations;

/// <summary>
/// Lê um campo de texto do `FINDINGS.jsonl` aceitando também um ARRAY de textos.
///
/// Existe porque uma linha escrita à mão com `"fix": ["...", "..."]` derrubava a
/// desserialização da linha INTEIRA: o defeito real (`OPS-070`, já corrigido) sumia do
/// gate e era substituído por um `PARSE-ERROR` sintético. Isto é, uma diferença de forma
/// apagava o conteúdo — a mesma família de "o envelope apaga a causa" que esta operação
/// já encontrou cinco vezes.
///
/// A tolerância é deliberadamente estreita: array de textos vira um parágrafo por item.
/// Qualquer outra forma continua sendo erro, porque um conversor que aceita tudo devolve
/// silêncio no lugar do defeito — que é exatamente o que se está tentando evitar.
/// </summary>
public sealed class FlexibleTextConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return reader.GetString();

            case JsonTokenType.StartArray:
                var parts = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new JsonException("Array de texto com item que não é texto.");
                    }

                    var item = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        parts.Add(item);
                    }
                }

                return parts.Count == 0 ? null : string.Join("\n\n", parts);

            default:
                throw new JsonException($"Esperado texto ou array de texto, veio {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value);
    }
}
