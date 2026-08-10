using Harness.Host.Agents;
using Microsoft.Extensions.Configuration;

namespace Harness.Host.V3;

public static class V3LegacyAutoDispatchPolicy
{
    public static bool IsV3Active(IConfiguration configuration) =>
        configuration.GetValue("Harness:V3:Active", true);

    public static bool ShouldStartLegacyCardDispatcher(
        AgentRunSettings settings,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!settings.Enabled ||
            string.IsNullOrWhiteSpace(settings.ControlledRoot) ||
            !settings.AutoDispatchEnabled)
        {
            return false;
        }

        if (!IsV3Active(configuration))
        {
            return true;
        }

        return configuration.GetValue("Harness:AgentRuns:LegacyAutoDispatchEnabled", false);
    }
}
