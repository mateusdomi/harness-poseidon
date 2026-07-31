using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harness.SharedKernel.Security;

/// <summary>
/// Um segredo chegou onde nunca deveria ter chegado e a escrita foi RECUSADA. Fail-closed: em um
/// canal de alto risco (ledger encadeado, receipt), persistir o valor seria irreversível — ele
/// entraria em backup, export e réplica antes de alguém perceber.
/// </summary>
public sealed class SecretPersistenceException(string channel)
    : Exception($"A secret survived sanitization and cannot be persisted to '{channel}'.")
{
    public string Channel { get; } = channel;
}

/// <summary>
/// Fase 0A3 (BR-014): a política ÚNICA de sanitização aplicada ANTES da persistência.
///
/// O produto já tinha <see cref="SecretTextProtector"/>, mas ele só rodava nas bordas — saída de
/// processo e exportação. O audit store serializava o detalhe recebido como veio, de modo que um
/// segredo podia ficar gravado no ledger, no banco e em todo backup tirado dali em diante, sendo
/// redigido apenas na hora de exibir. Redigir na saída é maquiagem: o dado já vazou para o disco.
///
/// Aqui a sanitização acontece antes do INSERT e antes do hash do ledger — o conteúdo persistido é
/// exatamente o conteúdo verificado.
/// </summary>
public static partial class PersistenceSanitizer
{
    private const string Redacted = "[REDACTED]";

    /// <summary>
    /// Sanitiza um payload JSON preservando a ESTRUTURA. Aplicar uma substituição de texto cru em
    /// JSON quebraria o documento (um valor casado no meio de aspas destrói o objeto), e um payload
    /// inválido derruba consumidores que hoje funcionam. Por isso o documento é percorrido: valores
    /// de campos sensíveis viram <c>[REDACTED]</c> inteiros e os demais textos passam pelo redator.
    /// Texto que não é JSON é redigido como texto.
    /// </summary>
    public static string SanitizeJson(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return payload ?? string.Empty;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return SecretTextProtector.Redact(payload);
        }

        using (document)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteSanitized(writer, document.RootElement, propertyName: null);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    /// <summary>Sanitiza texto livre (mensagem de erro, resumo, nome de arquivo).</summary>
    public static string SanitizeText(string? value) => SecretTextProtector.Redact(value);

    /// <summary>
    /// Canal de ALTO RISCO: sanitiza e, se ainda restar segredo reconhecível, RECUSA a escrita. É a
    /// diferença entre "tentamos redigir" e "não persistimos segredo".
    /// </summary>
    public static string SanitizeCriticalJson(string? payload, string channel)
    {
        var sanitized = SanitizeJson(payload);
        return SecretTextProtector.ContainsSecret(sanitized)
            ? throw new SecretPersistenceException(channel)
            : sanitized;
    }

    /// <summary>
    /// Um valor de texto pode ser, ele próprio, um JSON serializado — é o caso do `detail` da
    /// auditoria, que carrega o objeto do evento como string. Sem descer nele, um campo
    /// `"password":"x"` escaparia inteiro: o redator de texto procura `password=valor` ou
    /// `password: valor`, e em JSON existe uma aspa entre o nome e os dois-pontos. Foi por essa
    /// fresta que o canário estruturado passou na primeira versão deste bloco.
    /// </summary>
    private static string SanitizeStringValue(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
        {
            try
            {
                using var nested = JsonDocument.Parse(value);
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    WriteSanitized(writer, nested.RootElement, propertyName: null);
                }

                return Encoding.UTF8.GetString(buffer.WrittenSpan);
            }
            catch (JsonException)
            {
                // Parecia JSON e não era; segue como texto.
            }
        }

        return SecretTextProtector.Redact(value);
    }

    private static void WriteSanitized(
        Utf8JsonWriter writer, JsonElement element, string? propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteSanitized(writer, property.Value, property.Name);
                }

                writer.WriteEndObject();
                return;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteSanitized(writer, item, propertyName);
                }

                writer.WriteEndArray();
                return;

            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                // O NOME do campo já é evidência suficiente: um campo chamado `password` não tem
                // valor publicável, por mais que o conteúdo não case com nenhum padrão conhecido.
                writer.WriteStringValue(
                    propertyName is not null && SensitiveField().IsMatch(propertyName)
                        ? Redacted
                        : SanitizeStringValue(value));
                return;

            default:
                element.WriteTo(writer);
                return;
        }
    }

    [GeneratedRegex(
        "(?i)(password|passphrase|secret|token|api[_-]?key|apikey|credential|authorization|cookie|private[_-]?key|access[_-]?key)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SensitiveField();
}
