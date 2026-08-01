using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Harness.Host.Execution;
using Harness.Modules.Coordination.Application;

namespace Harness.Host.WorkBoard;

/// <summary>
/// Extrai fontes não confiáveis dentro da imagem governada, sem rede, root, capabilities ou
/// escrita persistente. Falha de ferramenta nunca vira conteúdo fabricado: retorna um estado
/// explícito que a Bruna é obrigada a comunicar.
/// </summary>
public sealed class SandboxedArtifactContentExtractor(IsolatedExecutionSettings settings)
    : IArtifactContentExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ArtifactContentExtraction> ExtractAsync(
        string fileName,
        string contentType,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(content);

        var root = Path.Combine(Path.GetTempPath(), $"poseidon-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var payload = Path.Combine(root, "payload");
        try
        {
            await File.WriteAllBytesAsync(payload, content, cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(payload, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var start = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var extractionType = CanonicalExtractionType(fileName, contentType);
            foreach (var argument in new[]
            {
                "run", "--rm", "--network", "none", "--read-only",
                "--tmpfs", "/tmp:rw,noexec,nosuid,size=128m",
                "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                "--memory", "512m", "--cpus", "1", "--pids-limit", "64",
                "--mount", $"type=bind,source={root},target=/input,readonly",
                settings.AgentImageName,
                "python3", "/opt/harness/artifact_extract.py", "/input/payload", extractionType,
            })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("The artifact sandbox did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                throw;
            }

            _ = await stderr;
            if (process.ExitCode != 0)
            {
                return Failed();
            }

            var contract = JsonSerializer.Deserialize<ExtractionContract>(await stdout, JsonOptions);
            return contract is null || string.IsNullOrWhiteSpace(contract.Status)
                ? Failed()
                : new ArtifactContentExtraction(contract.Status, contract.Text ?? string.Empty);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ArtifactContentExtraction(
                "extraction_timed_out",
                "[FONTE NÃO INTERPRETADA: a extração segura excedeu o tempo permitido.]");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or JsonException or Win32Exception)
        {
            return Failed();
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A limpeza não altera a verdade da extração; o diretório tem nome único,
                // contém somente a cópia temporária e será removido pelo sistema operacional.
            }
            catch (UnauthorizedAccessException)
            {
                // Mesmo contrato acima: uma falha de limpeza não fabrica nem perde evidência.
            }
        }
    }

    private static ArtifactContentExtraction Failed() => new(
        "extraction_failed",
        "[FONTE NÃO INTERPRETADA: o arquivo foi armazenado, mas a extração segura falhou.]");

    private static string CanonicalExtractionType(string fileName, string declared) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".zip" => "application/zip",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            _ => declared,
        };

    private sealed record ExtractionContract(string Status, string? Text);
}
