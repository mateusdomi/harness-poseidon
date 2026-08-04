using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Modules.Coordination.Contracts;

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

/// <summary>
/// POR QUE a revisão reprovou. Conjunto fechado, e a distinção existe para responder uma pergunta
/// que a operação não sabia responder: 73% do custo medido é retrabalho, e sem esta classificação
/// não havia como separar "faltou contexto ao ator" de "o critério do revisor é severo" de "o
/// enunciado estava errado". Três causas diferentes, três correções diferentes, uma métrica só.
///
/// <see cref="None"/> é o valor de quem NÃO reprovou — aprovação não tem causa de reprovação, e
/// deixar isso implícito faria "sem causa" e "causa desconhecida" colidirem no mesmo silêncio.
/// <see cref="Other"/> é o honesto para quando o classificador não soube dizer: ele preserva o
/// fato de que houve reprovação sem inventar um motivo que ninguém observou.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewRejectionCause>))]
public enum ReviewRejectionCause
{
    /// <summary>Não houve reprovação.</summary>
    None,

    /// <summary>O ator não recebeu o que precisava para executar — falha do pacote, não dele.</summary>
    ContextMissing,

    /// <summary>O trabalho rodou e não satisfez os critérios de aceite declarados.</summary>
    AcceptanceNotMet,

    /// <summary>A entrega saiu do escopo declarado do card.</summary>
    ScopeViolation,

    /// <summary>Satisfez o combinado, mas não a barra de qualidade do revisor.</summary>
    QualityBar,

    /// <summary>Reprovou e o classificador não soube dizer por quê. Honesto, nunca conveniente.</summary>
    Other,
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

    /// <summary>
    /// Causa tipada da reprovação, derivada do contrato do crítico. Só é populada quando
    /// <see cref="Verdict"/> é <see cref="CriticVerdict.Fail"/>; aprovações usam
    /// <see cref="ReviewRejectionCause.None"/>.
    /// </summary>
    public ReviewRejectionCause RejectionCause { get; init; } = ReviewRejectionCause.None;
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
    /// Também classifica a causa tipada da reprovação para persistência em
    /// <see cref="CriticReviewResult.RejectionCause"/>.
    /// </summary>
    public static (CriticVerdict Verdict, string ReasonCode, IReadOnlyList<CriticFinding> Findings, string? Summary, ReviewRejectionCause RejectionCause)
        Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return (CriticVerdict.Fail, "critic.no_output", [], null, ReviewRejectionCause.Other);
        }

        var json = ExtractJsonObject(output);
        if (json is null)
        {
            return (CriticVerdict.Fail, "critic.output_not_json", [], null, ReviewRejectionCause.Other);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("verdict", out var verdictElement) ||
                verdictElement.ValueKind != JsonValueKind.String)
            {
                return (CriticVerdict.Fail, "critic.verdict_missing", [], null, ReviewRejectionCause.Other);
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
            var checks = root.TryGetProperty("checks", out var checksElement) &&
                checksElement.ValueKind == JsonValueKind.Object
                ? checksElement
                : (JsonElement?)null;

            // Um PASS acompanhado de achado P0/P1 é incoerente: prevalece o achado.
            if (verdict == CriticVerdict.Pass &&
                findings.Any(finding => finding.Severity is CriticFindingSeverity.P0 or CriticFindingSeverity.P1))
            {
                return (CriticVerdict.Fail, "critic.pass_contradicted_by_findings", findings, summary,
                    ClassifyRejectionCause(checks, CriticVerdict.Fail, findings));
            }

            // Um resumo otimista não substitui a prova. Para aprovar, o revisor precisa
            // materializar o checklist que demonstra que comparou o pacote completo, o
            // escopo e a evidência. Em documentos, as duas listas tornam explícita a
            // auditoria de afirmações — uma tabela de proveniência no próprio artefato não
            // licencia repetir inferências como fatos em outras seções.
            if (verdict == CriticVerdict.Pass)
            {
                if (checks is null)
                {
                    return (CriticVerdict.Fail, "critic.checks_missing", findings, summary,
                        ReviewRejectionCause.Other);
                }

                if (!ReadTrue(checks.Value, "delegationCompared") ||
                    !ReadTrue(checks.Value, "scopeVerified") ||
                    !ReadTrue(checks.Value, "evidenceSufficient"))
                {
                    return (CriticVerdict.Fail, "critic.checks_failed", findings, summary,
                        ClassifyRejectionCause(checks, CriticVerdict.Fail, findings));
                }

                if (HasArrayItems(checks.Value, "unsupportedClaims"))
                {
                    return (CriticVerdict.Fail, "critic.unsupported_claims", findings, summary,
                        ReviewRejectionCause.QualityBar);
                }

                if (HasArrayItems(checks.Value, "unlabeledInferences"))
                {
                    return (CriticVerdict.Fail, "critic.unlabeled_inferences", findings, summary,
                        ReviewRejectionCause.QualityBar);
                }
            }

            var reasonCode = verdict == CriticVerdict.Pass ? "critic.pass" : "critic.fail";
            var cause = verdict == CriticVerdict.Pass
                ? ReviewRejectionCause.None
                : ClassifyRejectionCause(checks, CriticVerdict.Fail, findings);
            return (verdict, reasonCode, findings, summary, cause);
        }
        catch (JsonException)
        {
            return (CriticVerdict.Fail, "critic.output_not_json", [], null, ReviewRejectionCause.Other);
        }
    }

    private static ReviewRejectionCause ClassifyRejectionCause(
        JsonElement? checks,
        CriticVerdict verdict,
        IReadOnlyList<CriticFinding> findings)
    {
        if (verdict == CriticVerdict.Pass)
        {
            return ReviewRejectionCause.None;
        }

        if (checks is not null)
        {
            if (!ReadTrue(checks.Value, "evidenceSufficient"))
            {
                return ReviewRejectionCause.ContextMissing;
            }

            if (!ReadTrue(checks.Value, "scopeVerified"))
            {
                return ReviewRejectionCause.ScopeViolation;
            }

            if (HasArrayItems(checks.Value, "unsupportedClaims") ||
                HasArrayItems(checks.Value, "unlabeledInferences"))
            {
                return ReviewRejectionCause.QualityBar;
            }
        }

        foreach (var finding in findings)
        {
            var code = finding.Code.AsSpan();
            if (code.Contains("acceptance".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                code.Contains("criteria".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                code.Contains("requirement".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return ReviewRejectionCause.AcceptanceNotMet;
            }
        }

        return ReviewRejectionCause.Other;
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
