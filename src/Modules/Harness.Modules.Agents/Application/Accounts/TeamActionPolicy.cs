using System.Globalization;
using System.Text.RegularExpressions;

namespace Harness.Modules.Agents.Application.Accounts;

/// <summary>
/// Uma persona proposta pela chefe. É uma INTENÇÃO: nada aqui vira autoridade antes de passar
/// pela policy e pelo catálogo.
/// </summary>
public sealed record ProposedPersona(
    string Key,
    string Name,
    string Purpose,
    string Specialty,
    IReadOnlyList<string> Responsibilities,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> RiskTiers);

/// <summary>
/// Veredito da policy sobre uma proposta: aceita (possivelmente REDUZIDA), ou recusada com motivo
/// tipado. Reduzir em vez de recusar é deliberado — negar a persona inteira porque ela pediu uma
/// capability a mais entregaria o trabalho a um generalista, que é o resultado pior.
/// </summary>
public sealed record TeamActionVerdict(
    bool Allowed,
    string ReasonCode,
    ProposedPersona? Persona,
    IReadOnlyList<string> RemovedCapabilities);

/// <summary>
/// A fronteira entre "a chefe administra a própria equipe" e "a chefe amplia a própria
/// autoridade".
///
/// Criar especialista é gestão do Control Plane e passa a ser dela. O que continua fora do
/// alcance de qualquer texto que o modelo produza: conceder capability que a policy proíbe,
/// alcançar o canon, dispensar a segregação ator≠revisor e inventar ferramenta que não existe no
/// catálogo. A policy é PURA para poder ser provada sem banco; a existência real de tools é
/// conferida pelo serviço de aplicação, que enxerga o catálogo.
/// </summary>
public static class TeamActionPolicy
{
    /// <summary>Formato da chave: minúsculas, dígitos e hífen — igual ao catálogo semeado.</summary>
    private static readonly Regex KeyPattern =
        new("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Capabilities que NENHUMA persona recebe, tenha o pedido a justificativa que tiver. São as
    /// que dissolveriam as garantias do produto: escrever no canon, virar revisor de si mesma,
    /// falar direto com o usuário por fora do chefe, ou administrar o próprio catálogo.
    /// </summary>
    private static readonly HashSet<string> ForbiddenCapabilities = new(
        [
            "canon.write", "governance.write", "guardrails.write",
            "agents.admin", "agents.create", "policy.write",
            "self.review", "approve.own", "gate.approve",
            "user.publish", "channel.publish",
            "secrets.read", "credentials.read",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Faixas de risco reconhecidas; qualquer outra é descartada.</summary>
    private static readonly HashSet<string> RiskTiers =
        new(["low", "medium", "high", "critical"], StringComparer.OrdinalIgnoreCase);

    public static TeamActionVerdict Evaluate(ProposedPersona? persona)
    {
        if (persona is null)
        {
            return new TeamActionVerdict(false, "team.persona_missing", null, []);
        }

        var key = (persona.Key ?? string.Empty).Trim().ToLower(CultureInfo.InvariantCulture);
        if (key.Length is < 3 or > 100 || !KeyPattern.IsMatch(key))
        {
            return new TeamActionVerdict(false, "team.invalid_key", null, []);
        }

        if (string.IsNullOrWhiteSpace(persona.Name) ||
            string.IsNullOrWhiteSpace(persona.Purpose) ||
            string.IsNullOrWhiteSpace(persona.Specialty) ||
            persona.Responsibilities is null || persona.Responsibilities.Count == 0 ||
            persona.Constraints is null || persona.Constraints.Count == 0 ||
            persona.RequiredCapabilities is null || persona.RequiredCapabilities.Count == 0)
        {
            return new TeamActionVerdict(false, "team.incomplete_persona", null, []);
        }

        // Sem o PORQUÊ concreto não há como o dono auditar depois se a criação fazia sentido — e
        // uma persona criada "por precaução" é exatamente a explosão de catálogo que queremos
        // evitar.
        if (persona.Purpose.Trim().Length < 20)
        {
            return new TeamActionVerdict(false, "team.purpose_too_vague", null, []);
        }

        var requested = (persona.RequiredCapabilities ?? [])
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Select(capability => capability.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var removed = requested.Where(ForbiddenCapabilities.Contains).ToArray();
        var granted = requested.Where(capability => !ForbiddenCapabilities.Contains(capability)).ToArray();
        if (granted.Length == 0)
        {
            return new TeamActionVerdict(false, "team.no_safe_capability", null, removed);
        }

        var tiers = (persona.RiskTiers ?? [])
            .Where(tier => RiskTiers.Contains(tier))
            .Select(tier => tier.ToLower(CultureInfo.InvariantCulture))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new TeamActionVerdict(
            true,
            removed.Length > 0 ? "team.persona_reduced" : "team.persona_allowed",
            persona with
            {
                Key = key,
                Name = persona.Name.Trim(),
                Purpose = persona.Purpose.Trim(),
                Specialty = (persona.Specialty ?? string.Empty).Trim(),
                Responsibilities = Clean(persona.Responsibilities),
                Constraints = Clean(persona.Constraints),
                RequiredCapabilities = granted,
                // Sem faixa declarada, a persona nasce no risco mais BAIXO: menor privilégio
                // também vale para o tipo de trabalho que ela pode receber.
                RiskTiers = tiers.Length > 0 ? tiers : ["low"],
            },
            removed);
    }

    /// <summary>
    /// Uma persona já existente cobre a proposta? Evita que cada demanda pareça exigir um
    /// especialista novo e o catálogo vire uma lista de quase-duplicatas inúteis.
    ///
    /// A comparação é por ESPECIALIDADE e por sobreposição de propósito/responsabilidades —
    /// deliberadamente conservadora: na dúvida, reutiliza. Criar de novo é o caminho caro.
    /// </summary>
    public static bool IsCoveredBy(
        ProposedPersona persona, string existingKey, string? existingSpecialty, string existingDescription)
    {
        ArgumentNullException.ThrowIfNull(persona);
        if (string.Equals(existingKey, persona.Key, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(persona.Specialty) &&
            !string.IsNullOrWhiteSpace(existingSpecialty) &&
            string.Equals(existingSpecialty.Trim(), persona.Specialty.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var haystack = $"{existingKey} {existingSpecialty} {existingDescription}"
            .ToLower(CultureInfo.InvariantCulture);
        var terms = Tokenize($"{persona.Specialty} {persona.Purpose}");
        if (terms.Count == 0)
        {
            return false;
        }

        var hits = terms.Count(term => haystack.Contains(term, StringComparison.Ordinal));
        return hits * 2 >= terms.Count;
    }

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? values) =>
    [
        .. (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Take(20),
    ];

    /// <summary>
    /// Palavras significativas do propósito. Termos curtos e conectivos são ruído e fariam
    /// qualquer persona "cobrir" qualquer outra.
    /// </summary>
    private static IReadOnlyList<string> Tokenize(string text) =>
    [
        .. text
            .ToLower(CultureInfo.InvariantCulture)
            .Split([' ', ',', '.', ';', ':', '/', '-', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 5)
            .Distinct(StringComparer.Ordinal)
            .Take(12),
    ];
}
