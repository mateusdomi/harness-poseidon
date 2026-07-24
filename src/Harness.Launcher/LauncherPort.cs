using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Harness.Launcher;

/// <summary>
/// Resolve a porta de escuta local do Host de forma FIXA e PERSISTENTE (PORTA-DINAMICA).
///
/// Antes, ausente <c>--port</c>, o Launcher pedia porta 0 ao Kestrel — uma porta aleatória a
/// cada start, o que quebra o bookmark do dono. Agora, sem porta explícita, o Launcher reusa a
/// porta salva no primeiro start em <c>&lt;data-dir&gt;/port</c>. Se a porta salva estiver
/// ocupada, cai para uma porta livre próxima SÓ NAQUELE start (sem sobrescrever a preferência,
/// para o bookmark se auto-curar quando a porta preferida voltar a ficar livre). Uma porta
/// explícita (<c>--port</c>) vence tudo e nunca altera a preferência persistida.
/// </summary>
public static class LauncherPort
{
    /// <summary>Porta fixa padrão adotada e persistida no primeiro start.</summary>
    public const int DefaultPort = 5173;

    /// <summary>Quantas portas vizinhas varrer ao procurar um fallback.</summary>
    private const int FallbackScanRange = 64;

    public static string PortFilePath(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Path.Combine(dataDirectory, "port");
    }

    /// <summary>
    /// Resolve a porta a vincular. <paramref name="explicitPort"/> (de <c>--port</c>) vence e é
    /// devolvida como está — o chamador é responsável por qualquer checagem de ocupação. Sem porta
    /// explícita: reusa a persistida (fallback livre se ocupada) ou, no primeiro start, adota a
    /// <see cref="DefaultPort"/> (ou uma livre próxima) e a persiste.
    /// </summary>
    public static int Resolve(string dataDirectory, int? explicitPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (explicitPort is int requested)
        {
            return requested;
        }

        var portFile = PortFilePath(dataDirectory);
        var persisted = TryReadPersistedPort(portFile);
        if (persisted is int saved)
        {
            // Preferência estável: se estiver livre usa; se ocupada, desvio temporário para uma
            // porta livre SEM apagar a preferência salva (o bookmark volta quando ela liberar).
            return IsFree(saved) ? saved : FindFreePort(saved);
        }

        // Primeiro start: escolhe a porta fixa padrão (ou uma livre próxima) e a persiste.
        var chosen = IsFree(DefaultPort) ? DefaultPort : FindFreePort(DefaultPort);
        if (chosen != 0)
        {
            TryPersistPort(portFile, chosen);
        }

        return chosen;
    }

    /// <summary>Lê a porta persistida, se houver e for válida (1..65535).</summary>
    public static int? TryReadPersistedPort(string portFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portFile);
        if (!File.Exists(portFile))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(portFile).Trim();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                   && value is >= 1 and <= 65535
                ? value
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryPersistPort(string portFile, int port)
    {
        try
        {
            var directory = Path.GetDirectoryName(portFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = portFile + $".tmp-{Environment.ProcessId}";
            File.WriteAllText(temporary, port.ToString(CultureInfo.InvariantCulture));
            File.Move(temporary, portFile, overwrite: true);
        }
        catch (IOException)
        {
            // Persistência é best-effort: se não deu para gravar, o start continua nesta porta e
            // a próxima inicialização apenas repete a escolha do padrão.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Confere se a porta pode ser vinculada em 127.0.0.1 agora.</summary>
    public static bool IsFree(int port)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    /// <summary>
    /// Procura uma porta livre próxima de <paramref name="preferred"/> (acima e depois abaixo).
    /// Devolve 0 quando nenhuma foi encontrada na janela — 0 faz o Kestrel escolher dinamicamente,
    /// garantindo que o start nunca falhe por indisponibilidade de porta.
    /// </summary>
    public static int FindFreePort(int preferred)
    {
        for (var candidate = preferred + 1;
             candidate <= preferred + FallbackScanRange && candidate <= 65535;
             candidate++)
        {
            if (IsFree(candidate))
            {
                return candidate;
            }
        }

        for (var candidate = preferred - 1;
             candidate >= preferred - FallbackScanRange && candidate >= 1024;
             candidate--)
        {
            if (IsFree(candidate))
            {
                return candidate;
            }
        }

        return 0;
    }
}
