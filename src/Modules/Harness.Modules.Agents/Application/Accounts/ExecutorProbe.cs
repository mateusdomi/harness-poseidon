using System.Diagnostics;
using Harness.Modules.Agents.Contracts;

namespace Harness.Modules.Agents.Application.Accounts;

public sealed record ExecutorProbeResult(
    string ExecutorId,
    bool Installed,
    string? DetectedVersion,
    AgentAccountState State,
    string ReasonCode);

/// <summary>
/// Probe real de executores externos (CA-4). Executa o binário com `--version` num ambiente
/// mínimo e reporta o que foi OBSERVADO. Um executor ausente vira
/// <see cref="AgentAccountState.Unavailable"/> — nunca se declara suporte que não existe.
///
/// O probe nunca recebe nem imprime segredo: o ambiente do subprocesso é montado por
/// allowlist e a saída é truncada a uma linha de versão.
/// </summary>
public sealed class ExecutorProbe
{
    private readonly TimeSpan _timeout;

    public ExecutorProbe(TimeSpan? timeout = null) =>
        _timeout = timeout ?? TimeSpan.FromSeconds(20);

    public async Task<ExecutorProbeResult> ProbeAsync(
        ExecutorProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = profile.Command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--version");
            // Ambiente mínimo por allowlist: nenhuma variável herdada por engano.
            startInfo.Environment.Clear();
            foreach (var name in profile.EnvironmentAllowlist)
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value))
                {
                    startInfo.Environment[name] = value;
                }
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ExecutorProbeResult(
                    profile.ExecutorId, false, null,
                    AgentAccountState.Unavailable, "executor.start_failed");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                return new ExecutorProbeResult(
                    profile.ExecutorId, false, null,
                    AgentAccountState.Unavailable, "executor.probe_failed");
            }

            var version = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return new ExecutorProbeResult(
                profile.ExecutorId, true, version,
                AgentAccountState.Available, "executor.available");
        }
        catch (OperationCanceledException)
        {
            return new ExecutorProbeResult(
                profile.ExecutorId, false, null,
                AgentAccountState.Unavailable, "executor.probe_timeout");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or FileNotFoundException or InvalidOperationException)
        {
            // Somente o TIPO do erro é relevante; a mensagem pode conter caminho local.
            return new ExecutorProbeResult(
                profile.ExecutorId, false, null,
                AgentAccountState.Unavailable, "executor.not_installed");
        }
    }
}
