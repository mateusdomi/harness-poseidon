using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Host.Agents;

/// <summary>Severidade de um achado do critic. Conjunto fechado, P0 é o mais grave.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CriticFindingSeverity>))]
public enum CriticFindingSeverity
{
    P0,
    P1,
    P2,
    P3,
}

/// <summary>Veredito do critic. `Fail` é o padrão quando falta evidência.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CriticVerdict>))]
public enum CriticVerdict
{
    Fail,
    Pass,
}

public sealed record CriticFinding(
    CriticFindingSeverity Severity,
    string Code,
    string Summary,
    string? Path,
    string? Evidence);

/// <summary>
/// Resultado auditável de uma revisão independente (CA-7).
///
/// `Default-FAIL`: sem saída válida do critic, sem evidência ou com o executor falhando, o
/// veredito é <see cref="CriticVerdict.Fail"/> — nunca "passou por omissão".
/// </summary>
public sealed record CriticReviewResult(
    string ReviewId,
    string AttemptId,
    string CriticAlias,
    string CriticExecutorId,
    string ActorAlias,
    CriticVerdict Verdict,
    string ReasonCode,
    IReadOnlyList<CriticFinding> Findings,
    string? Summary,
    long? AccountFencingToken,
    long DurationMs)
{
    public bool Approved => Verdict == CriticVerdict.Pass;
}

/// <summary>
/// Contrato de saída exigido do critic. O schema é ESTRITO: propriedades desconhecidas são
/// recusadas, para que o veredito não venha de texto livre interpretado.
/// </summary>
public static class CriticReviewContract
{
    public const string SchemaJson = """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["verdict", "summary", "findings", "checks"],
      "properties": {
        "verdict": { "type": "string", "enum": ["pass", "fail"] },
        "summary": { "type": "string", "maxLength": 4000 },
        "findings": {
          "type": "array",
          "maxItems": 50,
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["severity", "code", "summary"],
            "properties": {
              "severity": { "type": "string", "enum": ["P0", "P1", "P2", "P3"] },
              "code": { "type": "string", "maxLength": 120 },
              "summary": { "type": "string", "maxLength": 2000 },
              "path": { "type": "string", "maxLength": 400 },
              "evidence": { "type": "string", "maxLength": 2000 }
            }
          }
        },
        "checks": {
          "type": "object",
          "additionalProperties": false,
          "required": [
            "delegationCompared", "scopeVerified", "evidenceSufficient",
            "unsupportedClaims", "unlabeledInferences"
          ],
          "properties": {
            "delegationCompared": { "type": "boolean" },
            "scopeVerified": { "type": "boolean" },
            "evidenceSufficient": { "type": "boolean" },
            "unsupportedClaims": {
              "type": "array", "maxItems": 50,
              "items": { "type": "string", "maxLength": 1000 }
            },
            "unlabeledInferences": {
              "type": "array", "maxItems": 50,
              "items": { "type": "string", "maxLength": 1000 }
            }
          }
        }
      }
    }
    """;

    /// <summary>
    /// Extrai o veredito da saída do critic. Qualquer desvio — JSON ausente, inválido,
    /// veredito fora do conjunto — resulta em FAIL com código tipado, jamais em PASS.
    /// </summary>
    public static (CriticVerdict Verdict, string ReasonCode, IReadOnlyList<CriticFinding> Findings, string? Summary)
        Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return (CriticVerdict.Fail, "critic.no_output", [], null);
        }

        var json = ExtractJsonObject(output);
        if (json is null)
        {
            return (CriticVerdict.Fail, "critic.output_not_json", [], null);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("verdict", out var verdictElement) ||
                verdictElement.ValueKind != JsonValueKind.String)
            {
                return (CriticVerdict.Fail, "critic.verdict_missing", [], null);
            }

            var findings = new List<CriticFinding>();
            if (root.TryGetProperty("findings", out var findingsElement) &&
                findingsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in findingsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var severity = ReadString(item, "severity") ?? "P2";
                    findings.Add(new CriticFinding(
                        Enum.TryParse<CriticFindingSeverity>(severity, ignoreCase: true, out var parsed)
                            ? parsed
                            : CriticFindingSeverity.P2,
                        ReadString(item, "code") ?? "critic.unspecified",
                        ReadString(item, "summary") ?? string.Empty,
                        ReadString(item, "path"),
                        ReadString(item, "evidence")));
                }
            }

            var summary = ReadString(root, "summary");
            var verdict = string.Equals(verdictElement.GetString(), "pass", StringComparison.OrdinalIgnoreCase)
                ? CriticVerdict.Pass
                : CriticVerdict.Fail;

            // Um PASS acompanhado de achado P0/P1 é incoerente: prevalece o achado.
            if (verdict == CriticVerdict.Pass &&
                findings.Any(finding => finding.Severity is CriticFindingSeverity.P0 or CriticFindingSeverity.P1))
            {
                return (CriticVerdict.Fail, "critic.pass_contradicted_by_findings", findings, summary);
            }

            // Um resumo otimista não substitui a prova. Para aprovar, o revisor precisa
            // materializar o checklist que demonstra que comparou o pacote completo, o
            // escopo e a evidência. Em documentos, as duas listas tornam explícita a
            // auditoria de afirmações — uma tabela de proveniência no próprio artefato não
            // licencia repetir inferências como fatos em outras seções.
            if (verdict == CriticVerdict.Pass)
            {
                if (!root.TryGetProperty("checks", out var checks) ||
                    checks.ValueKind != JsonValueKind.Object)
                {
                    return (CriticVerdict.Fail, "critic.checks_missing", findings, summary);
                }

                if (!ReadTrue(checks, "delegationCompared") ||
                    !ReadTrue(checks, "scopeVerified") ||
                    !ReadTrue(checks, "evidenceSufficient"))
                {
                    return (CriticVerdict.Fail, "critic.checks_failed", findings, summary);
                }

                if (HasArrayItems(checks, "unsupportedClaims"))
                {
                    return (CriticVerdict.Fail, "critic.unsupported_claims", findings, summary);
                }

                if (HasArrayItems(checks, "unlabeledInferences"))
                {
                    return (CriticVerdict.Fail, "critic.unlabeled_inferences", findings, summary);
                }
            }

            return (verdict, verdict == CriticVerdict.Pass ? "critic.pass" : "critic.fail", findings, summary);
        }
        catch (JsonException)
        {
            return (CriticVerdict.Fail, "critic.output_not_json", [], null);
        }
    }

    private static bool ReadTrue(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static bool HasArrayItems(JsonElement parent, string property) =>
        !parent.TryGetProperty(property, out var value) ||
        value.ValueKind != JsonValueKind.Array ||
        value.GetArrayLength() > 0;

    /// <summary>Isola o último objeto JSON da saída, tolerando texto ao redor.</summary>
    private static string? ExtractJsonObject(string output)
    {
        var end = output.LastIndexOf('}');
        if (end < 0)
        {
            return null;
        }

        var depth = 0;
        for (var index = end; index >= 0; index--)
        {
            if (output[index] == '}')
            {
                depth++;
            }
            else if (output[index] == '{')
            {
                depth--;
                if (depth == 0)
                {
                    return output[index..(end + 1)];
                }
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
