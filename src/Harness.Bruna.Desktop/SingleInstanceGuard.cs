using System;
using System.IO;
using System.Threading;

namespace Harness.Bruna.Desktop;

/// <summary>
/// Garante que apenas uma instância do Bruna Desktop Companion execute por data-dir.
/// Usa um file lock cross-platform (funciona em macOS e Windows).
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly FileStream _lockStream;
    private bool _disposed;

    private SingleInstanceGuard(FileStream lockStream)
    {
        _lockStream = lockStream;
    }

    public static SingleInstanceGuard? Acquire(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var lockDirectory = Path.Combine(dataDirectory, "bruna");
        Directory.CreateDirectory(lockDirectory);
        var lockPath = Path.Combine(lockDirectory, "bruna.lock");

        try
        {
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                writer.Write(Environment.ProcessId);
                writer.Flush();
            }
            stream.Position = 0;
            return new SingleInstanceGuard(stream);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lockStream.Dispose();
    }
}
