namespace Harness.Modules.Workflows.Product;

/// <summary>De onde um valor de NFR veio. A ordem é a força: fonte explícita vence default.</summary>
public enum NfrProvenance
{
    /// <summary>A fonte de requisitos disse. Não é premissa: é requisito.</summary>
    RequirementSource,

    /// <summary>Default da organização, aplicado como PREMISSA registrada.</summary>
    OrganizationDefault,

    /// <summary>Baseline do Poseidon, aplicado como PREMISSA registrada.</summary>
    Baseline,

    /// <summary>Ninguém respondeu e não há default seguro: a dimensão está aberta.</summary>
    Unanswered,
}

/// <summary>Um valor operacional com a proveniência que o sustenta.</summary>
public sealed record NfrValue(string? Value, NfrProvenance Provenance)
{
    public bool IsAssumption => Provenance is NfrProvenance.OrganizationDefault or NfrProvenance.Baseline;

    public static NfrValue Unanswered { get; } = new(null, NfrProvenance.Unanswered);
}

/// <summary>
/// As condições operacionais do produto — o que decide estratégia de teste, arquitetura de carga e
/// operação, e que no run real simplesmente não existia como pergunta.
///
/// O desenho segue a mesma regra do perfil efetivo: <b>nenhum número é inventado em silêncio</b>.
/// Cada dimensão carrega proveniência; default aplicado é PREMISSA registrada, e dimensão sem
/// resposta e sem default fica declaradamente aberta — o plano de teste deriva do que está aqui,
/// nunca de um valor que alguém chutou dentro de um script de carga.
/// </summary>
public sealed record OperationalProfile(
    NfrValue ExpectedUsers,
    NfrValue ConcurrentUsers,
    NfrValue PeakLoad,
    NfrValue DataVolume,
    NfrValue Availability,
    NfrValue LatencyTarget,
    NfrValue Rto,
    NfrValue Rpo,
    NfrValue SecurityClassification,
    NfrValue PersonalData,
    NfrValue Accessibility,
    NfrValue BrowserTargets,
    NfrValue Retention)
{
    /// <summary>As dimensões que continuam sem resposta — o insumo honesto do plano de perguntas.</summary>
    public IReadOnlyList<string> Open =>
        [.. All().Where(item => item.Value.Provenance == NfrProvenance.Unanswered)
            .Select(item => item.Name)];

    /// <summary>As premissas aplicadas — o que precisa aparecer registrado, não escondido.</summary>
    public IReadOnlyList<string> Assumptions =>
        [.. All().Where(item => item.Value.IsAssumption)
            .Select(item => $"{item.Name}={item.Value.Value} ({item.Value.Provenance})")];

    /// <summary>
    /// Teste de carga só se justifica com carga RESPONDIDA ou assumida de fonte declarada. Sem
    /// concorrência conhecida, exigir load test é exigir um número inventado — e o resultado seria
    /// uma prova sobre um cenário que ninguém pediu.
    /// </summary>
    public bool LoadTestApplicable =>
        ConcurrentUsers.Provenance != NfrProvenance.Unanswered ||
        PeakLoad.Provenance != NfrProvenance.Unanswered;

    private IEnumerable<(string Name, NfrValue Value)> All() =>
    [
        ("expected_users", ExpectedUsers), ("concurrent_users", ConcurrentUsers),
        ("peak_load", PeakLoad), ("data_volume", DataVolume), ("availability", Availability),
        ("latency_target", LatencyTarget), ("rto", Rto), ("rpo", Rpo),
        ("security_classification", SecurityClassification), ("personal_data", PersonalData),
        ("accessibility", Accessibility), ("browser_targets", BrowserTargets),
        ("retention", Retention),
    ];
}

/// <summary>
/// Resolve o perfil operacional na ordem que evita os dois erros: perguntar vinte questões de uma
/// vez, e inventar um número em silêncio.
///
/// <code>fonte de requisitos → default da organização → baseline → aberto (declarado)</code>
///
/// Só o que muda materialmente arquitetura ou estratégia de teste merece virar pergunta — e essa
/// decisão é da <see cref="DecisionPolicy"/>, não daqui.
/// </summary>
public static class OperationalProfileResolver
{
    /// <summary>
    /// O baseline conservador do Poseidon para produto Web interno: os defaults que permitem
    /// começar sem mentir. Acessibilidade tem default (WCAG 2.2 AA) porque é padrão de qualidade;
    /// carga NÃO tem, porque um número de concorrência inventado calibraria um teste inteiro para
    /// o cenário errado.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Baseline { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["availability"] = "horário comercial, sem alta disponibilidade formal",
            ["accessibility"] = "WCAG 2.2 AA",
            ["browser_targets"] = "navegadores evergreen (Chrome, Edge, Firefox, Safari)",
            ["security_classification"] = "interno",
            ["retention"] = "5 anos",
        };

    public static OperationalProfile Resolve(
        IReadOnlyDictionary<string, string> fromRequirements,
        IReadOnlyDictionary<string, string>? organizationDefaults = null)
    {
        ArgumentNullException.ThrowIfNull(fromRequirements);
        var org = organizationDefaults ?? new Dictionary<string, string>(StringComparer.Ordinal);

        NfrValue Get(string key) =>
            fromRequirements.TryGetValue(key, out var explicitValue)
                ? new NfrValue(explicitValue, NfrProvenance.RequirementSource)
                : org.TryGetValue(key, out var orgValue)
                    ? new NfrValue(orgValue, NfrProvenance.OrganizationDefault)
                    : Baseline.TryGetValue(key, out var baseValue)
                        ? new NfrValue(baseValue, NfrProvenance.Baseline)
                        : NfrValue.Unanswered;

        return new OperationalProfile(
            Get("expected_users"), Get("concurrent_users"), Get("peak_load"), Get("data_volume"),
            Get("availability"), Get("latency_target"), Get("rto"), Get("rpo"),
            Get("security_classification"), Get("personal_data"), Get("accessibility"),
            Get("browser_targets"), Get("retention"));
    }
}
