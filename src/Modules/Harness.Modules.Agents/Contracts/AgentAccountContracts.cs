using System.Text.Json.Serialization;

namespace Harness.Modules.Agents.Contracts;

/// <summary>
/// Estado operacional de uma conta de agente (CA-2/CA-6). Conjunto fechado: o scheduler
/// nunca infere disponibilidade por ausência de dado.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentAccountState>))]
public enum AgentAccountState
{
    /// <summary>Pronta para receber trabalho.</summary>
    Available,

    /// <summary>Reservada para um attempt específico, ainda não em execução.</summary>
    Reserved,

    /// <summary>Executando um attempt.</summary>
    Running,

    /// <summary>Em cooldown após cota/erro; volta sozinha ao fim da janela.</summary>
    CoolingDown,

    /// <summary>Cota esgotada na janela corrente.</summary>
    QuotaLimited,

    /// <summary>Credencial ausente, expirada ou revogada: exige ação humana.</summary>
    AuthenticationRequired,

    /// <summary>Operacional porém com sinal de saúde reduzido.</summary>
    Degraded,

    /// <summary>Desabilitada por decisão administrativa.</summary>
    Disabled,

    /// <summary>Executor não instalado ou incapaz de rodar nesta máquina.</summary>
    Unavailable,
}

/// <summary>Saúde observada do executor por probe real, nunca presumida.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentAccountHealth>))]
public enum AgentAccountHealth
{
    Unknown,
    Healthy,
    Degraded,
    Unhealthy,
}

/// <summary>
/// Referência OPACA a uma credencial. O valor do segredo nunca trafega por este contrato,
/// nunca é persistido no banco e nunca entra em log, evento ou evidência: apenas o
/// identificador do item no secret store (`keychain://`, `secret://`, `env://`).
/// </summary>
public sealed record CredentialReference(string Scheme, string Locator)
{
    public static readonly string[] AllowedSchemes = ["keychain", "secret", "env"];

    public override string ToString() => $"{Scheme}://{Locator}";
}

/// <summary>Capacidades declaradas de um executor/conta, usadas pelo roteamento.</summary>
public sealed record CapabilitySet(
    IReadOnlyList<string> Capabilities,
    bool SupportsStreaming,
    bool SupportsResume,
    bool SupportsEffort,
    IReadOnlyList<string> EffortValues,
    long? MaxContextTokens);

/// <summary>Cota observada de uma conta. Sem número inventado: nulo significa desconhecido.</summary>
public sealed record QuotaSnapshot(
    decimal? LimitUsd,
    decimal? UsedUsd,
    DateTimeOffset? WindowResetsAt,
    DateTimeOffset ObservedAt)
{
    public decimal? RemainingUsd => LimitUsd is null || UsedUsd is null
        ? null
        : Math.Max(0m, LimitUsd.Value - UsedUsd.Value);
}

/// <summary>
/// Perfil de um executor externo (CLI real instalada). Os campos refletem a CLI observada
/// por probe — comando, flags de execução não interativa e variável de isolamento — e não
/// nomes inventados.
/// </summary>
public sealed record ExecutorProfile(
    string ExecutorId,
    string DisplayName,
    string Command,
    IReadOnlyList<string> NonInteractiveArguments,
    string? ConfigHomeEnvironmentVariable,
    IReadOnlyList<string> EnvironmentAllowlist,
    CapabilitySet Capabilities,
    string? DetectedVersion);

/// <summary>
/// Conta de agente identificada por ALIAS. Nenhum e-mail, token ou senha é persistido:
/// a associação alias → conta real vive apenas na configuração local protegida.
/// </summary>
public sealed record AgentAccountContract(
    string Alias,
    string ProviderKind,
    string ExecutorId,
    string CredentialReference,
    string ConfigHomeReference,
    IReadOnlyList<string> AllowedRoles,
    IReadOnlyList<string> AllowedPathScopes,
    AgentAccountState State,
    AgentAccountHealth Health,
    int ConcurrencyLimit,
    int ActiveAttempts,
    string? CurrentAttemptId,
    QuotaSnapshot? Quota,
    DateTimeOffset? CooldownUntil,
    DateTimeOffset? LastSuccessfulSmokeAt,
    string? FailureReason,
    int Priority);

/// <summary>Concessão de uso exclusivo de uma conta por um attempt, com fencing.</summary>
public sealed record AccountLeaseContract(
    string Alias,
    string AttemptId,
    string OwnerId,
    long FencingToken,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Decisão de roteamento explicável (CA-6): por que esta conta foi escolhida, quais foram
/// consideradas e por que as demais foram descartadas — códigos, nunca texto livre.
/// </summary>
public sealed record AccountSelectionDecision(
    string? SelectedAlias,
    string ReasonCode,
    IReadOnlyList<AccountSelectionCandidate> Candidates,
    IReadOnlyList<string> FallbackAliases)
{
    /// <summary>
    /// O snapshot de cota (N4/5.1, contrato de quota) que fundamentou a conta selecionada —
    /// nulo quando nenhuma foi selecionada ou nenhum snapshot foi observado para o alias. A
    /// decisão de rota precisa expor QUAL medição usou, para auditoria e para que o operador
    /// possa distinguir uma escolha fundamentada em dado fresco de uma sem cota observada.
    /// </summary>
    public AccountQuotaSnapshot? SelectedQuotaSnapshot =>
        SelectedAlias is null
            ? null
            : Candidates
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Alias, SelectedAlias, StringComparison.OrdinalIgnoreCase))
                ?.QuotaSnapshotUsed;
}

public sealed record AccountSelectionCandidate(
    string Alias,
    bool Eligible,
    string ReasonCode,
    int Priority,

    /// <summary>
    /// Desempate entre ELEGÍVEIS de mesma prioridade: 0 é a primeira escolha e valores maiores
    /// só são usados na falta de alternativa. Existe porque "elegível" não é binário na prática —
    /// uma conta perto do limite de cota executa, mas prefere-se não gastar nela o trabalho longo
    /// enquanto houver conta com margem. Nunca bloqueia: capacidade real não é descartada.
    /// </summary>
    int PreferenceRank = 0,

    /// <summary>
    /// O snapshot de cota consultado para este alias (N4/5.1), nulo quando nenhum foi
    /// observado. Presente independentemente do veredito — para que a razão de recusa ou de
    /// escolha seja rastreável até a medição concreta que a fundamentou.
    /// </summary>
    AccountQuotaSnapshot? QuotaSnapshotUsed = null);
