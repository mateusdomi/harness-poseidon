using System.Diagnostics;

namespace Harness.Launcher;

/// <summary>
/// 0-E (decisão do proprietário, 31/07/2026): o contêiner é PRÉ-REQUISITO. O launcher verifica ANTES
/// de subir o Host — descobrir isso só quando o primeiro agente falha desperdiça o tempo do dono e
/// transforma um pré-requisito claro em erro obscuro no meio do trabalho.
/// </summary>
internal static class ContainerRuntimeCheck
{
    /// <summary>Mensagem de NEGÓCIO, igual à da API e à do doctor: diz o que fazer.</summary>
    public const string Message =
        "Preciso do Docker para trabalhar com segurança — instale ou inicie o Docker e me chame de novo.";

    public static bool IsAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList = { "version", "--format", "{{.Server.Version}}" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            // O cliente responde mesmo com o daemon parado; o que conta é o SERVIDOR ter versão.
            return process.ExitCode == 0 &&
                !string.IsNullOrWhiteSpace(process.StandardOutput.ReadToEnd().Trim());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }
}
