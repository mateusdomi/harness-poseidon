using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Modules.Agents.Contracts;

namespace Harness.Modules.Agents.Application.Accounts;

/// <summary>
/// Registro DURÁVEL de disponibilidade por conta — sobrevive a restart do Host. É o que
/// permite "salvar a data/hora que a conta volta" quando a cota esgota (janela de 5h do
/// Claude/Codex, semanal do Kimi) e o retry inteligente saber quantas falhas seguidas houve.
///
/// Fora do repositório (ex.: `~/.harness/account-availability.json`); nunca guarda segredo,
/// só alias, estado fechado, cooldown e um código de motivo.
/// </summary>
public sealed class AccountAvailabilityLedger
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly object _sync = new();
    private readonly string _path;

    public AccountAvailabilityLedger(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath))
        {
            throw new AgentAccountValidationException("availability.path_must_be_absolute");
        }

        _path = Path.GetFullPath(filePath);
    }

    /// <summary>Caminho padrão do operador: `<home>/.harness/account-availability.json`.</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".harness",
            "account-availability.json");

    public AccountAvailabilityRecord? Get(string alias)
    {
        AgentAccountRegistry.ValidateAlias(alias);
        lock (_sync)
        {
            return Read().GetValueOrDefault(alias);
        }
    }

    public IReadOnlyList<AccountAvailabilityRecord> List()
    {
        lock (_sync)
        {
            return [.. Read().Values.OrderBy(record => record.Alias, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Cota esgotada: a conta fica <see cref="AgentAccountState.QuotaLimited"/> até
    /// <paramref name="until"/>. Quando a janela real é conhecida (raro headless), passe-a;
    /// senão use um cooldown conservador. Nunca inventa percentual de cota, só quando volta.
    /// </summary>
    public void MarkQuotaLimited(string alias, DateTimeOffset until, string reasonCode, DateTimeOffset now) =>
        Mutate(alias, existing => new AccountAvailabilityRecord(
            alias, AgentAccountState.QuotaLimited, until, reasonCode,
            (existing?.ConsecutiveFailures ?? 0) + 1, now));

    /// <summary>Login necessário: exige ação humana; sem cooldown (não volta sozinha).</summary>
    public void MarkAuthenticationRequired(string alias, string reasonCode, DateTimeOffset now) =>
        Mutate(alias, existing => new AccountAvailabilityRecord(
            alias, AgentAccountState.AuthenticationRequired, null, reasonCode,
            (existing?.ConsecutiveFailures ?? 0) + 1, now));

    /// <summary>Falha transitória: conta em cooldown curto, contando falhas seguidas para o backoff.</summary>
    public void RecordTransientFailure(string alias, DateTimeOffset until, string reasonCode, DateTimeOffset now) =>
        Mutate(alias, existing => new AccountAvailabilityRecord(
            alias, AgentAccountState.CoolingDown, until, reasonCode,
            (existing?.ConsecutiveFailures ?? 0) + 1, now));

    /// <summary>Sucesso: zera o histórico de falhas e volta a disponível.</summary>
    public void MarkAvailable(string alias, DateTimeOffset now) =>
        Mutate(alias, _ => new AccountAvailabilityRecord(
            alias, AgentAccountState.Available, null, "availability.available", 0, now));

    /// <summary>
    /// Reabilita as contas cujo cooldown já venceu e devolve os aliases recuperados — é o
    /// passo que o agendador chama para retomar o trabalho quando a janela reseta. Contas
    /// <see cref="AgentAccountState.AuthenticationRequired"/> NÃO voltam sozinhas.
    /// </summary>
    public IReadOnlyList<string> RecoverExpired(DateTimeOffset now)
    {
        lock (_sync)
        {
            var state = Read();
            var recovered = new List<string>();
            foreach (var record in state.Values.ToArray())
            {
                if (record.CooldownUntil is { } until && until <= now &&
                    record.State is AgentAccountState.QuotaLimited or AgentAccountState.CoolingDown)
                {
                    state[record.Alias] = record with
                    {
                        State = AgentAccountState.Available,
                        CooldownUntil = null,
                        ReasonCode = "availability.recovered",
                        UpdatedAt = now,
                    };
                    recovered.Add(record.Alias);
                }
            }

            if (recovered.Count > 0)
            {
                Write(state);
            }

            return recovered;
        }
    }

    private void Mutate(string alias, Func<AccountAvailabilityRecord?, AccountAvailabilityRecord> update)
    {
        AgentAccountRegistry.ValidateAlias(alias);
        lock (_sync)
        {
            var state = Read();
            state[alias] = update(state.GetValueOrDefault(alias));
            Write(state);
        }
    }

    private Dictionary<string, AccountAvailabilityRecord> Read()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, AccountAvailabilityRecord>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var records = JsonSerializer.Deserialize<List<AccountAvailabilityRecord>>(
                File.ReadAllText(_path), Json) ?? [];
            return records.ToDictionary(record => record.Alias, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, AccountAvailabilityRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Write(Dictionary<string, AccountAvailabilityRecord> state)
    {
        var directory = Path.GetDirectoryName(_path);
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        var ordered = state.Values.OrderBy(record => record.Alias, StringComparer.Ordinal).ToList();
        var temporary = $"{_path}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(ordered, Json));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, OwnerOnly);
        }

        File.Move(temporary, _path, overwrite: true);
    }
}

/// <summary>
/// Disponibilidade persistida de uma conta. Sem segredo: alias, estado fechado, quando volta,
/// motivo tipado e quantas falhas seguidas (para o backoff).
/// </summary>
public sealed record AccountAvailabilityRecord(
    string Alias,
    AgentAccountState State,
    DateTimeOffset? CooldownUntil,
    string ReasonCode,
    int ConsecutiveFailures,
    DateTimeOffset UpdatedAt);
