using System.Diagnostics;
using System.Text.Json;

namespace Harness.OperationSupervisor;

/// <summary>
/// Exclusividade do supervisor sobre um repositório, com heartbeat em arquivo.
///
/// Dois supervisores no mesmo working tree lançariam duas Integradoras que editam os mesmos
/// arquivos — o resultado não é o dobro de trabalho, é trabalho perdido. O lease é um
/// arquivo simples porque precisa ser legível por quem acordar de madrugada sem subir nada:
/// `cat LEASE.json` responde quem está vivo, desde quando e em que ciclo.
///
/// A retomada depois de uma queda é feita por PID e não por prazo: um lease órfão (processo
/// morto) é tomado na hora, enquanto um lease de processo vivo nunca é. Prazo sozinho
/// devolveria o lease a um segundo supervisor enquanto o primeiro ainda trabalha.
/// </summary>
public sealed class SupervisorLease : IDisposable
{
    private readonly string _path;
    private readonly int _pid;
    private readonly DateTimeOffset _acquiredAt;
    private bool _released;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private SupervisorLease(string path, int pid, DateTimeOffset acquiredAt)
    {
        _path = path;
        _pid = pid;
        _acquiredAt = acquiredAt;
    }

    public static SupervisorLease? TryAcquire(string root)
    {
        var path = Path.Combine(root, "LEASE.json");

        if (File.Exists(path) && IsHeldByALiveProcess(path))
        {
            return null;
        }

        var lease = new SupervisorLease(path, Environment.ProcessId, DateTimeOffset.UtcNow);
        lease.Write(cycle: 0, gate: "starting");
        return lease;
    }

    private static bool IsHeldByALiveProcess(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("pid", out var pidElement))
            {
                return false;
            }

            var pid = pidElement.GetInt32();
            if (pid == Environment.ProcessId)
            {
                return false;
            }

            using var _ = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            // Processo não existe mais: lease órfão, pode ser tomado.
            return false;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // Lease ilegível não pode travar a operação inteira — mas o motivo fica no log.
            Console.Error.WriteLine($"LEASE.json ilegível ({exception.Message}); assumindo órfão.");
            return false;
        }
    }

    public void Heartbeat(int cycle, string gate) => Write(cycle, gate);

    private void Write(int cycle, string gate)
    {
        var payload = new
        {
            pid = _pid,
            host = Environment.MachineName,
            acquiredAt = _acquiredAt,
            heartbeatAt = DateTimeOffset.UtcNow,
            cycle,
            gate,
        };

        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(payload, Options) + Environment.NewLine);
        }
        catch (IOException exception)
        {
            // Perder um heartbeat não justifica derrubar a supervisão da madrugada.
            Console.Error.WriteLine($"heartbeat não gravado: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;

        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
            // Lease sobrando é tomado por PID morto na próxima aquisição.
        }
    }
}
