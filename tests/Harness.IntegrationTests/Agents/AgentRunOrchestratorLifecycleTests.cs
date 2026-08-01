using Harness.Host;
using Harness.Host.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// Regressão do shutdown que deixava CLIs de agentes reparentadas ao PID 1: possuir métodos de
/// cancelamento não basta; a MESMA instância que mantém os runs precisa estar registrada no ciclo
/// de vida do Host para receber StopAsync antes do descarte dos stores.
/// </summary>
public sealed class AgentRunOrchestratorLifecycleTests
{
    [Fact]
    public async Task LiveRunOwnerIsTheHostedShutdownParticipant()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory, "orchestrator-lifecycle", Guid.NewGuid().ToString("N"));
        var controlledRoot = Path.Combine(root, "controlled");
        Directory.CreateDirectory(controlledRoot);

        try
        {
            await using var app = HostApplication.Build(
            [
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", Path.Combine(root, "poseidon.db"),
                "--Harness:AgentRuns:Enabled", "true",
                "--Harness:IsolatedExecution:Mode", "Fake",
                "--Harness:AgentRuns:ControlledRoot", controlledRoot,
                "--Harness:AgentRuns:AccountsFilePath", Path.Combine(root, "no-accounts.json"),
                "--Harness:AgentRuns:ProfilesRoot", Path.Combine(root, "profiles"),
                "--Harness:AgentRuns:AvailabilityLedgerPath", Path.Combine(root, "availability.json"),
                "--Harness:AgentRuns:AutoDispatchEnabled", "false",
            ]);

            var owner = app.Services.GetRequiredService<AgentRunOrchestrator>();
            var lifecycleParticipants = app.Services.GetServices<IHostedService>();

            Assert.Contains(lifecycleParticipants, participant => ReferenceEquals(owner, participant));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
