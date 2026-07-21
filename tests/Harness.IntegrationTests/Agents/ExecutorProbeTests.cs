using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// CA-4: probe REAL dos executores externos. Reporta o que foi observado nesta máquina;
/// executor ausente é `Unavailable`, nunca "suportado". Não exige credencial.
/// </summary>
public sealed class ExecutorProbeTests
{
    [Fact]
    public async Task ProbeReportsObservedInstallationForEveryCatalogedExecutor()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var probe = new ExecutorProbe();
        foreach (var profile in ExecutorCatalog.All)
        {
            var result = await probe.ProbeAsync(profile, timeout.Token);
            Assert.Equal(profile.ExecutorId, result.ExecutorId);
            // O probe é honesto: instalado ⇒ versão observada; ausente ⇒ Unavailable.
            if (result.Installed)
            {
                Assert.False(string.IsNullOrWhiteSpace(result.DetectedVersion));
                Assert.Equal(AgentAccountState.Available, result.State);
            }
            else
            {
                Assert.Equal(AgentAccountState.Unavailable, result.State);
                Assert.Null(result.DetectedVersion);
            }

            Console.WriteLine(
                $"probe {result.ExecutorId}: installed={result.Installed} " +
                $"reason={result.ReasonCode} version={result.DetectedVersion ?? "-"}");
        }
    }

    [Fact]
    public async Task ProbeOfAnAbsentExecutorIsUnavailableAndNeverThrows()
    {
        var absent = new ExecutorProfile(
            "does-not-exist", "Absent", "poseidon-nonexistent-cli", [], null, ["PATH"],
            new CapabilitySet([], false, false, false, [], null), null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await new ExecutorProbe(TimeSpan.FromSeconds(5))
            .ProbeAsync(absent, timeout.Token);
        Assert.False(result.Installed);
        Assert.Equal(AgentAccountState.Unavailable, result.State);
    }
}
