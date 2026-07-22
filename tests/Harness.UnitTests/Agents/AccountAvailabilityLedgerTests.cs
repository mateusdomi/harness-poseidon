using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// O registro DURÁVEL de disponibilidade: salva quando a conta volta (cota/cooldown),
/// sobrevive a restart, conta falhas seguidas para o backoff e recupera sozinho quando a
/// janela vence — exceto login, que exige humano.
/// </summary>
public sealed class AccountAvailabilityLedgerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = System.IO.Path.Combine(
        Path.GetTempPath(), $"harness-availability-{Guid.NewGuid():N}");

    private string LedgerPath() => System.IO.Path.Combine(_dir, "account-availability.json");

    [Fact]
    public void QuotaExhaustionSavesTheReturnTimeAndSurvivesAReopen()
    {
        var until = Now.AddHours(3);
        new AccountAvailabilityLedger(LedgerPath()).MarkQuotaLimited(
            "worker-glm-general", until, "run.quota_exhausted", Now);

        // Um NOVO ledger (simula restart do Host) lê o mesmo arquivo.
        var reopened = new AccountAvailabilityLedger(LedgerPath()).Get("worker-glm-general")!;
        Assert.Equal(AgentAccountState.QuotaLimited, reopened.State);
        Assert.Equal(until, reopened.CooldownUntil);
        Assert.Equal(1, reopened.ConsecutiveFailures);
    }

    [Fact]
    public void TheWeeklyKimiLimitKeepsItsExactReturnDateTime()
    {
        var mondayReset = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
        var ledger = new AccountAvailabilityLedger(LedgerPath());
        ledger.MarkQuotaLimited("worker-kimi-ui", mondayReset, "run.quota_exhausted", Now);

        Assert.Equal(mondayReset, ledger.Get("worker-kimi-ui")!.CooldownUntil);
        // Antes do reset, não recupera.
        Assert.Empty(ledger.RecoverExpired(Now.AddDays(2)));
        // No/depois do reset, volta.
        Assert.Equal(["worker-kimi-ui"], ledger.RecoverExpired(mondayReset));
        Assert.Equal(AgentAccountState.Available, ledger.Get("worker-kimi-ui")!.State);
    }

    [Fact]
    public void ConsecutiveTransientFailuresAccumulateForBackoff()
    {
        var ledger = new AccountAvailabilityLedger(LedgerPath());
        ledger.RecordTransientFailure("worker-glm-general", Now.AddMinutes(1), "run.transient_failure", Now);
        ledger.RecordTransientFailure("worker-glm-general", Now.AddMinutes(2), "run.transient_failure", Now.AddMinutes(1));

        Assert.Equal(2, ledger.Get("worker-glm-general")!.ConsecutiveFailures);
        // Sucesso zera o histórico.
        ledger.MarkAvailable("worker-glm-general", Now.AddMinutes(3));
        Assert.Equal(0, ledger.Get("worker-glm-general")!.ConsecutiveFailures);
    }

    [Fact]
    public void AuthenticationRequiredNeverRecoversOnItsOwn()
    {
        var ledger = new AccountAvailabilityLedger(LedgerPath());
        ledger.MarkAuthenticationRequired("worker-claude-secondary", "run.authentication_required", Now);

        Assert.Null(ledger.Get("worker-claude-secondary")!.CooldownUntil);
        // Mesmo muito depois, não volta sozinha: exige login humano.
        Assert.Empty(ledger.RecoverExpired(Now.AddDays(30)));
        Assert.Equal(
            AgentAccountState.AuthenticationRequired,
            ledger.Get("worker-claude-secondary")!.State);
    }

    [Fact]
    public void RecoveryReturnsOnlyAccountsWhoseWindowActuallyExpired()
    {
        var ledger = new AccountAvailabilityLedger(LedgerPath());
        ledger.MarkQuotaLimited("a-back", Now.AddHours(1), "run.quota_exhausted", Now);
        ledger.RecordTransientFailure("b-cooling", Now.AddMinutes(5), "run.transient_failure", Now);
        ledger.MarkQuotaLimited("c-still-limited", Now.AddHours(6), "run.quota_exhausted", Now);

        var recovered = ledger.RecoverExpired(Now.AddHours(2));
        Assert.Equal(["a-back", "b-cooling"], recovered.OrderBy(alias => alias));
        Assert.Equal(AgentAccountState.QuotaLimited, ledger.Get("c-still-limited")!.State);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
