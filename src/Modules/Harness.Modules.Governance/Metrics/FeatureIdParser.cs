using System.Text.RegularExpressions;

namespace Harness.Modules.Governance.Metrics;

/// <summary>
/// PLAT-04: deriva o identificador de feature estável a partir do título da tarefa/demanda.
///
/// O board embute IDs estáveis como <c>CAT-04</c> / <c>PLAT-04</c> no título. Esta associação é
/// PURA e determinística: reconhece um token líder no formato <c>[ID]</c>, <c>ID/Tnn</c>,
/// <c>ID:</c> ou <c>ID </c> (letras maiúsculas, hífen, dígitos). Títulos sem token reconhecível
/// caem no balde <see cref="Unassigned"/> — nunca inventamos um id.
/// </summary>
public static partial class FeatureIdParser
{
    public const string Unassigned = "unassigned";

    // Um token de feature: 2+ letras maiúsculas, hífen, 1..4 dígitos. Opcionalmente precedido de
    // '[' e seguido por ']', ':', '/', espaço ou fim. Ancorado ao início (ignorando espaços).
    [GeneratedRegex(@"^\s*\[?\s*(?<id>[A-Z]{2,}-\d{1,4})(?=\s|\]|:|/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex FeatureTokenRegex();

    /// <summary>Retorna o id da feature em maiúsculas, ou <see cref="Unassigned"/> se ausente.</summary>
    public static string Parse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Unassigned;
        }

        var match = FeatureTokenRegex().Match(title);
        return match.Success ? match.Groups["id"].Value.ToUpperInvariant() : Unassigned;
    }

    /// <summary>True quando o título carrega um token de feature reconhecível.</summary>
    public static bool TryParse(string? title, out string featureId)
    {
        featureId = Parse(title);
        return !string.Equals(featureId, Unassigned, StringComparison.Ordinal);
    }
}
