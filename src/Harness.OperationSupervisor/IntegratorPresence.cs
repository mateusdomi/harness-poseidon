using System.Diagnostics;
using System.Text.Json;

namespace Harness.OperationSupervisor;

/// <summary>
/// Quem é a Integradora ativa agora, por PID.
///
/// O supervisor sabe controlar as sessões que ELE lança, mas a primeira Integradora da noite
/// costuma ser uma sessão que o proprietário abriu à mão — e que o supervisor não vê. Sem
/// este registro, subir o supervisor durante uma sessão interativa produz duas Integradoras
/// editando o mesmo working tree, que é como se perde trabalho e não como se ganha
/// paralelismo (§12: MAX_INTEGRATOR_CLAUDE = 1).
///
/// Com ele, o supervisor sobe agora, fica observando, e só lança a próxima quando o PID
/// registrado morrer — que é exatamente o "Claude sai → recarregar estado → relançar" do §7,
/// sem depender de ninguém digitar nada.
/// </summary>
public static class IntegratorPresence
{
    private const string FileName = "INTEGRATOR.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Registra o processo informado como a Integradora ativa.</summary>
    public static void Claim(string root, int pid, string source)
    {
        var payload = new { pid, source, since = DateTimeOffset.UtcNow };
        File.WriteAllText(
            Path.Combine(root, FileName),
            JsonSerializer.Serialize(payload, Options) + Environment.NewLine);
    }

    public static void Release(string root)
    {
        var path = Path.Combine(root, FileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// PID da Integradora viva, ou null. Registro apontando para processo morto é limpo aqui
    /// mesmo: um arquivo órfão que bloqueasse o relance para sempre seria pior que não ter
    /// arquivo nenhum.
    /// </summary>
    public static int? ActivePid(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var pid = document.RootElement.GetProperty("pid").GetInt32();
            using var _ = Process.GetProcessById(pid);
            return pid;
        }
        catch (ArgumentException)
        {
            File.Delete(path);
            return null;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or IOException)
        {
            Console.Error.WriteLine($"{FileName} ilegível ({exception.Message}); tratado como ausente.");
            return null;
        }
    }
}
