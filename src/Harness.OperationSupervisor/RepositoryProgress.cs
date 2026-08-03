using System.Diagnostics;
using System.Globalization;

namespace Harness.OperationSupervisor;

/// <summary>
/// Há quanto tempo o repositório não anda.
///
/// É o sinal de trabalho da Integradora que não mente. Processo vivo não serve — foi a lição
/// mais cara desta operação —, uso de CPU também não (uma CLI presa em retry de telemetria
/// consome CPU indefinidamente), e o tamanho do log muito menos. Commit é efeito durável: se
/// a Integradora está trabalhando, o `develop` anda.
/// </summary>
internal static class RepositoryProgress
{
    /// <summary>
    /// Tempo desde o último commit em HEAD, ou <see langword="null"/> quando não dá para saber
    /// (sem git, fora de repositório, saída inesperada). Nulo NUNCA vira "está parado": na
    /// dúvida, o supervisor observa em vez de tomar a vaga de quem pode estar trabalhando.
    /// </summary>
    internal static TimeSpan? SinceLastCommit(string workingDirectory, DateTimeOffset now)
    {
        try
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("log");
            info.ArgumentList.Add("-1");
            info.ArgumentList.Add("--format=%ct");

            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(5_000) || process.ExitCode != 0)
            {
                return null;
            }

            if (!long.TryParse(output, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                return null;
            }

            var elapsed = now - DateTimeOffset.FromUnixTimeSeconds(seconds);
            return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
