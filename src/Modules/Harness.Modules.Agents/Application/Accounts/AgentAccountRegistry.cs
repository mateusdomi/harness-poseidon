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

    /// <summary>
    /// Hidrata o registro (memória do processo) com a disponibilidade JÁ OBSERVADA no ledger
    /// durável. Toda conta nasce <c>AuthenticationRequired</c> — disponibilidade é comprovada,
    /// nunca presumida — mas uma conta cuja disponibilidade já foi PROVADA antes do restart não
    /// pode voltar a exigir prova manual: sem este elo, todo reinício do Host devolvia a frota
    /// inteira para `authentication-required` e o loop do chefe adiava cada card com
    /// `no_eligible_account` até um humano rodar o doctor de novo.
    ///
    /// Contas desabilitadas na configuração continuam desabilitadas: o ledger observa
    /// disponibilidade, jamais reabilita o que o operador desligou.
    /// </summary>
    public int ApplyObservedAvailability(IReadOnlyList<AccountAvailabilityRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var applied = 0;
        foreach (var record in records)
        {
            if (!_accounts.TryGetValue(record.Alias, out var account) ||
                account.State == AgentAccountState.Disabled)
            {
                continue;
            }

            _accounts[account.Alias] = account with
            {
                State = record.State,
                Health = record.State switch
                {
                    AgentAccountState.Available => AgentAccountHealth.Healthy,
                    AgentAccountState.QuotaLimited or AgentAccountState.CoolingDown
                        or AgentAccountState.Degraded => AgentAccountHealth.Degraded,
                    _ => AgentAccountHealth.Unknown,
                },
            };
            applied++;
        }

        return applied;
    }

    public AgentAccountContract? Get(string alias) =>
        _accounts.TryGetValue(alias, out var account) ? account : null;

    public AgentAccountContract Register(AgentAccountContract account)
    {
        ArgumentNullException.ThrowIfNull(account);
        ValidateAlias(account.Alias);
        ValidateCredentialReference(account.CredentialReference);
        ValidateRoles(account.AllowedRoles);
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
    /// CAT-09/ADR-022: o PAPEL de runtime é o eixo de escopo/claim e pertence ao conjunto
    /// canônico fechado (<see cref="AgentRoles.IsKnown"/>): chief-orchestrator,
    /// frontend-specialist, backend-specialist, critic. Um "novo papel de negócio" NÃO vira um
    /// enum arbitrário — ele é modelado como especialidade/template/persona no catálogo de
    /// agentes. Uma conta que declare um papel fora do conjunto é rejeitada de forma tipada, em
    /// vez de silenciosamente conceder (ou negar) escopo com base num rótulo desconhecido.
    /// </summary>
    public static void ValidateRoles(IReadOnlyList<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        foreach (var role in roles)
        {
            if (!AgentRoles.IsKnown(role))
            {
                throw new AgentAccountValidationException("account.role_unknown");
            }
        }
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
