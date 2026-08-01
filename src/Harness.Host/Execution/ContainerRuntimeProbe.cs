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

    /// <summary>
    /// Mensagem para quando o Docker está de pé mas a IMAGEM do agente não existe.
    ///
    /// É um estado diferente e precisava de mensagem própria: o piloto real mostrou o Docker
    /// rodando, o doctor verde e TODA execução recusada com `sandbox_required` — vinte e uma
    /// tentativas do mesmo card, canceladas em milissegundos, sem que nada dissesse que faltava
    /// uma imagem. "Docker disponível" e "posso executar" não são a mesma afirmação.
    /// </summary>
    private static readonly System.Text.CompositeFormat MissingImageFormat =
        System.Text.CompositeFormat.Parse(MissingImageMessage);

    /// <summary>A mensagem de imagem ausente, já com o nome da imagem que falta.</summary>
    public static string MissingImage(string imageName) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, MissingImageFormat, imageName);

    public const string MissingImageMessage =
        "O Docker está funcionando, mas a imagem de execução dos agentes ainda não existe nesta " +
        "máquina — sem ela eu não consigo trabalhar em ambiente isolado. Construa a imagem " +
        "'{0}' e me chame de novo.";

    private static readonly Lazy<bool> Available = new(Detect, isThreadSafe: true);

    public static bool IsAvailable() => Available.Value;

    /// <summary>
    /// A imagem existe localmente? Sem ela o contêiner não sobe, a atestação resolve como não
    /// verificada e toda execução é recusada — corretamente, mas sem que ninguém saiba por quê.
    /// </summary>
    public static bool HasImage(string imageName)
    {
        if (string.IsNullOrWhiteSpace(imageName))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList = { "image", "inspect", imageName, "--format", "{{.Id}}" },
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

            return process.ExitCode == 0;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException
                or System.IO.IOException)
        {
            return false;
        }
    }

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
