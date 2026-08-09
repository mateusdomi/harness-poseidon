using System;
using System.Collections.Generic;
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

    private readonly string? _installDirectory;

    public BrunaPhotoProcessor(string dataDirectory, string? installDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = dataDirectory;
        _installDirectory = installDirectory;
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

    private async Task ProcessAsync(string source, string destination, CancellationToken cancellationToken)
    {
        try
        {
            var pythonPath = ResolvePythonWithRembg();
            if (pythonPath is null)
            {
                Debug.WriteLine("rembg não encontrado; foto customizada será exibida com máscara oval.");
                return;
            }

            var temporary = destination + ".tmp";
            var script =
                "from rembg import remove;" +
                "from PIL import Image;" +
                "img = Image.open(open('" + EscapeForPythonOneLine(source) + "', 'rb'));" +
                "out = remove(img);" +
                "out.save('" + EscapeForPythonOneLine(temporary) + "');";

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
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

    /// <summary>
    /// Procura um interpretador Python que tenha o pacote <c>rembg</c> instalado.
    /// </summary>
    private string? ResolvePythonWithRembg()
    {
        var candidates = new List<string>();

        if (!string.IsNullOrEmpty(_installDirectory))
        {
            candidates.Add(Path.Combine(_installDirectory, ".venv-bruna", "bin", "python"));
            candidates.Add(Path.Combine(_installDirectory, ".venv-bruna", "bin", "python3"));
        }

        candidates.Add(Path.Combine(_dataDirectory, ".venv-bruna", "bin", "python"));
        candidates.Add(Path.Combine(_dataDirectory, ".venv-bruna", "bin", "python3"));

        var executableDirectory = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(executableDirectory, ".venv-bruna", "bin", "python"));
        candidates.Add(Path.Combine(executableDirectory, ".venv-bruna", "bin", "python3"));
        candidates.Add(Path.Combine(executableDirectory, "..", ".venv-bruna", "bin", "python"));
        candidates.Add(Path.Combine(executableDirectory, "..", ".venv-bruna", "bin", "python3"));

        candidates.Add("python3");
        candidates.Add("python");

        foreach (var candidate in candidates)
        {
            var resolved = candidate;
            try
            {
                resolved = Path.GetFullPath(resolved);
            }
            catch
            {
                // Ignora caminhos relativos inválidos.
            }

            if (!File.Exists(resolved) && candidate != "python3" && candidate != "python")
            {
                continue;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = resolved,
                    Arguments = "-c \"import rembg; print('ok')\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    continue;
                }

                process.WaitForExit(TimeSpan.FromSeconds(30));
                if (process.ExitCode == 0)
                {
                    return resolved;
                }
            }
            catch
            {
                // Tenta o próximo candidato.
            }
        }

        return null;
    }

    private static string EscapeForPythonOneLine(string path) =>
        path.Replace("\\", "\\\\").Replace("'", "\\'");
}
