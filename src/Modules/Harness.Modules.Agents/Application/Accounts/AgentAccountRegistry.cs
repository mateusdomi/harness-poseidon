using Harness.Modules.Agents.Contracts;

namespace Harness.Modules.Agents.Application.Accounts;

/// <summary>Erro de validação do registro de contas; nunca carrega segredo.</summary>
public sealed class AgentAccountValidationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Registro de contas de agente por ALIAS (CA-2).
///
/// Regras invioláveis:
/// - nenhum e-mail, token ou senha é aceito, validado ou persistido aqui;
/// - a credencial é sempre uma referência opaca (`keychain://`, `secret://`, `env://`);
/// - a associação alias → conta real vive apenas na configuração local protegida;
/// - a reserva de conta usa fencing crescente, para que um dono antigo não escreva depois
///   de perder a concessão.
/// </summary>
public sealed class AgentAccountRegistry
{
    private readonly Dictionary<string, AgentAccountContract> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AccountLeaseContract> _leases = new(StringComparer.OrdinalIgnoreCase);
    private long _fencing;

    /// <summary>Aliases canônicos sugeridos; a associação real é configurável.</summary>
    public static IReadOnlyList<string> SuggestedAliases { get; } =
    [
        "chief-claude-primary",
        "worker-claude-secondary",
        "worker-codex-frontend",
        "worker-codex-critic",
        "worker-kimi-ui",
        "worker-glm-general",
        "worker-antigravity-review",
    ];

    public IReadOnlyList<AgentAccountContract> List() =>
        _accounts.Values.OrderBy(account => account.Alias, StringComparer.Ordinal).ToArray();

    public AgentAccountContract? Get(string alias) =>
        _accounts.TryGetValue(alias, out var account) ? account : null;

    public AgentAccountContract Register(AgentAccountContract account)
    {
        ArgumentNullException.ThrowIfNull(account);
        ValidateAlias(account.Alias);
        ValidateCredentialReference(account.CredentialReference);
        if (ExecutorCatalog.Find(account.ExecutorId) is null)
        {
            throw new AgentAccountValidationException("account.executor_unknown");
        }

        if (account.ConcurrencyLimit < 1)
        {
            throw new AgentAccountValidationException("account.concurrency_invalid");
        }

        _accounts[account.Alias] = account;
        return account;
    }

    /// <summary>
    /// Um alias é um identificador técnico estável: minúsculas, dígitos e hífen. Um valor
    /// contendo `@` seria um e-mail — rejeitado explicitamente, pois identidade real de
    /// usuário nunca entra no registro.
    /// </summary>
    public static void ValidateAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias) || alias.Length > 100)
        {
            throw new AgentAccountValidationException("account.alias_invalid");
        }

        if (alias.Contains('@', StringComparison.Ordinal))
        {
            throw new AgentAccountValidationException("account.alias_must_not_be_email");
        }

        if (!alias.All(character =>
                char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-'))
        {
            throw new AgentAccountValidationException("account.alias_invalid");
        }
    }

    /// <summary>
    /// Aceita somente referência opaca com esquema allowlisted. Um valor que aparente ser o
    /// próprio segredo (sem esquema) é recusado — o segredo pertence ao secret store.
    /// </summary>
    public static void ValidateCredentialReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new AgentAccountValidationException("account.credential_reference_required");
        }

        var separator = reference.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            throw new AgentAccountValidationException("account.credential_reference_must_be_opaque");
        }

        var scheme = reference[..separator];
        if (!CredentialReference.AllowedSchemes.Contains(scheme, StringComparer.Ordinal))
        {
            throw new AgentAccountValidationException("account.credential_reference_scheme_denied");
        }

        if (reference[(separator + 3)..].Length == 0)
        {
            throw new AgentAccountValidationException("account.credential_reference_required");
        }
    }

    /// <summary>Reserva exclusiva com fencing crescente; recusa conta indisponível.</summary>
    public AccountLeaseContract Reserve(
        string alias, string attemptId, string ownerId, DateTimeOffset now, TimeSpan duration)
    {
        var account = Get(alias) ?? throw new AgentAccountValidationException("account.not_found");
        if (_leases.TryGetValue(alias, out var current) && current.ExpiresAt > now)
        {
            throw new AgentAccountValidationException("account.already_reserved");
        }

        if (account.State is not (AgentAccountState.Available or AgentAccountState.Reserved))
        {
            throw new AgentAccountValidationException("account.not_available");
        }

        if (account.ActiveAttempts >= account.ConcurrencyLimit)
        {
            throw new AgentAccountValidationException("account.concurrency_exhausted");
        }

        var lease = new AccountLeaseContract(
            alias, attemptId, ownerId, ++_fencing, now, now.Add(duration));
        _leases[alias] = lease;
        _accounts[alias] = account with
        {
            State = AgentAccountState.Reserved,
            CurrentAttemptId = attemptId,
            ActiveAttempts = account.ActiveAttempts + 1,
        };
        return lease;
    }

    /// <summary>Libera a concessão; um fencing antigo nunca libera a concessão vigente.</summary>
    public void Release(string alias, long fencingToken)
    {
        var account = Get(alias) ?? throw new AgentAccountValidationException("account.not_found");
        if (!_leases.TryGetValue(alias, out var lease))
        {
            return;
        }

        if (lease.FencingToken != fencingToken)
        {
            throw new AgentAccountValidationException("account.fencing_conflict");
        }

        _leases.Remove(alias);
        _accounts[alias] = account with
        {
            State = AgentAccountState.Available,
            CurrentAttemptId = null,
            ActiveAttempts = Math.Max(0, account.ActiveAttempts - 1),
        };
    }

    public AccountLeaseContract? GetLease(string alias) =>
        _leases.TryGetValue(alias, out var lease) ? lease : null;

    /// <summary>Coloca a conta em cooldown por cota, preservando o motivo tipado.</summary>
    public void MarkQuotaLimited(string alias, DateTimeOffset until, string reasonCode)
    {
        var account = Get(alias) ?? throw new AgentAccountValidationException("account.not_found");
        _accounts[alias] = account with
        {
            State = AgentAccountState.QuotaLimited,
            CooldownUntil = until,
            FailureReason = reasonCode,
        };
    }

    public void UpdateHealth(
        string alias, AgentAccountHealth health, AgentAccountState state, string? failureReason)
    {
        var account = Get(alias) ?? throw new AgentAccountValidationException("account.not_found");
        _accounts[alias] = account with
        {
            Health = health,
            State = state,
            FailureReason = failureReason,
        };
    }
}
