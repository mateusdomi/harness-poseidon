using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Harness.Bruna.Desktop;

/// <summary>
/// Processa a foto customizada da Bruna (ex.: upload via perfil de liderança do Poseidon),
/// removendo o fundo quando <c>rembg</c> está disponível. O resultado é cacheado no data-dir
/// para evitar reprocessamento a cada inicialização.
/// </summary>
public sealed class BrunaPhotoProcessor
{
    private readonly string _dataDirectory;

    public BrunaPhotoProcessor(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = dataDirectory;
    }

    /// <summary>
    /// Procura uma foto customizada no data-dir e devolve o caminho de uma versão processada
    /// (sem fundo). Se o processamento ainda não foi feito, inicia-o em background e devolve
    /// <c>null</c> para que o chamador use o asset embutido enquanto isso.
    /// </summary>
    public string? TryGetProcessedPhoto()
    {
        var source = ResolveCustomPhotoSource();
        if (source is null)
        {
            return null;
        }

        var cacheDirectory = Path.Combine(_dataDirectory, "bruna");
        Directory.CreateDirectory(cacheDirectory);
        var processed = Path.Combine(cacheDirectory, "processed-bruna.png");

        if (File.Exists(processed) && File.GetLastWriteTimeUtc(processed) >= File.GetLastWriteTimeUtc(source))
        {
            return processed;
        }

        _ = Task.Run(() => ProcessAsync(source, processed, CancellationToken.None));
        return null;
    }

    public string? ResolveCustomPhotoSource()
    {
        var assetsDirectory = Path.Combine(_dataDirectory, "assets");
        if (!Directory.Exists(assetsDirectory))
        {
            return null;
        }

        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp" })
        {
            var path = Path.Combine(assetsDirectory, $"bruna-magalhaes{extension}");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static async Task ProcessAsync(string source, string destination, CancellationToken cancellationToken)
    {
        try
        {
            var temporary = destination + ".tmp";
            var script =
                "from rembg import remove;" +
                "from PIL import Image;" +
                "img = Image.open(open('" + EscapeForPythonOneLine(source) + "', 'rb'));" +
                "out = remove(img);" +
                "out.save('" + EscapeForPythonOneLine(temporary) + "');";

            var startInfo = new ProcessStartInfo
            {
                FileName = "python3",
                Arguments = $"-c \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                Debug.WriteLine($"rembg falhou: {error}");
                return;
            }

            if (File.Exists(temporary))
            {
                File.Move(temporary, destination, overwrite: true);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Falha ao processar foto da Bruna: {exception.Message}");
        }
    }

    private static string EscapeForPythonOneLine(string path) =>
        path.Replace("\\", "\\\\").Replace("'", "\\'");
}
