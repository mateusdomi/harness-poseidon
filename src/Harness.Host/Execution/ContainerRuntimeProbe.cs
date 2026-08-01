using System.Diagnostics;

namespace Harness.Host.Execution;

/// <summary>
/// 0-E (decisão do proprietário, 31/07/2026): o contêiner é PRÉ-REQUISITO nos dois modos.
///
/// Antes existia um aceite de risco que dispensava a sandbox "até configurar uma". Uma exceção
/// temporária que o produto aceita vira permanente na prática — e era o último caminho que deixava
/// um agente produzir efeito no host sem fronteira nenhuma. Agora a ausência do runtime é uma
/// RECUSA com instrução, não um convite a abrir mão da contenção.
///
/// A sonda é barata e memoizada: perguntar ao sistema operacional a cada requisição custaria mais
/// do que o próprio trabalho que ela protege.
/// </summary>
public static class ContainerRuntimeProbe
{
    /// <summary>
    /// A mensagem é de NEGÓCIO e acionável — quem lê precisa saber o que fazer, não decifrar um
    /// código de erro. Ela sai igual no launcher, no doctor e na API.
    /// </summary>
    public const string Message =
        "Preciso do Docker para trabalhar com segurança — instale ou inicie o Docker e me chame de novo.";

    private static readonly Lazy<bool> Available = new(Detect, isThreadSafe: true);

    public static bool IsAvailable() => Available.Value;

    private static bool Detect()
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

            // `docker version` responde rápido quando o daemon está de pé e trava quando ele está
            // subindo. Esperar indefinidamente aqui seguraria o boot do Host.
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            // O binário existir não basta: o cliente responde mesmo com o daemon parado. O que
            // conta é o SERVIDOR ter versão — é ele que cria o contêiner.
            return process.ExitCode == 0 &&
                !string.IsNullOrWhiteSpace(process.StandardOutput.ReadToEnd().Trim());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }
}
