using System.Text.RegularExpressions;

namespace Harness.Host.Agents;

/// <summary>
/// Impede a Bruna de anunciar um efeito de equipe antes de o Control Plane produzi-lo e projeta o
/// resultado real em linguagem de negócio. A saída do modelo é intenção; a confirmação vem do
/// store, depois de definição, rota e instância do projeto existirem.
/// </summary>
public static partial class ChiefTeamActionResponse
{
    public static bool ContainsPrematureCompletionClaim(string response) =>
        PrematureCompletionClaim().IsMatch(response ?? string.Empty);

    public static string Project(
        string response,
        IReadOnlyList<ChiefTeamActionResult> results)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            return response;
        }

        var failed = results.Where(result => !Succeeded(result)).ToArray();
        var status = failed.Length == 0
            ? "A organização da equipe foi confirmada: as pessoas especializadas necessárias estão disponíveis no projeto e prontas para receber o trabalho."
            : "Ainda não consegui concluir toda a organização da equipe. Vou manter as partes afetadas sinalizadas e seguir com o que não depende delas; se surgir uma barreira que eu não consiga resolver, explico a decisão de negócio necessária.";
        return $"{response.Trim()}\n\n{status}";
    }

    private static bool Succeeded(ChiefTeamActionResult result) =>
        result.ReasonCode is "team.persona_allowed" or "team.persona_reduced" or
            "team.already_exists" or "team.reused_existing" or
            "team.reused_existing_added_to_project" or "team.observation" or
            "team.quarantined" or "team.active" or "team.reusable";

    [GeneratedRegex(
        @"\b(criei|criamos|adicionei|adicionamos|incorporei|incorporamos|contratei|contratamos)\b|\b(j[aá]\s+est[aá]|est[aá]\s+pront[oa])\b.{0,50}\b(equipe|especialista|profissional|pessoa)\b|\b(equipe|especialista|profissional|pessoa)\b.{0,50}\b(j[aá]\s+est[aá]|foi\s+(criad[oa]|adicionad[oa]|incorporad[oa]))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex PrematureCompletionClaim();
}
