using System.Diagnostics;
using System.Text;

namespace Harness.Host.Agents;

public sealed record ObjectiveBuildProbeResult(bool Ran, bool Succeeded, string Detail);

/// <summary>
/// Prova de COMPILAÇÃO executada pela PLATAFORMA no review de um card-objetivo.
///
/// Existe porque o buraco foi observado DUAS vezes no mesmo dia (2026-08-07): os gates
/// executáveis dependem de superfície declarada pelo produto (.sln/manifesto), o produto não a
/// declarava, o gate nunca rodava, o validador é read-only — e duas entregas que NÃO COMPILAVAM
/// foram aprovadas e integradas ao main. Um produto que não compila não é entregável em nenhuma
/// hipótese; essa verificação não pode depender de quem é verificado.
///
/// A sonda acha a superfície sozinha: .sln na raiz ou em src/ quando existir; senão, todos os
/// .csproj de primeiro nível sob src/. Sem superfície .NET, declara que não rodou — ausência de
/// backend .NET é assunto do gate de conformidade de stack, não desta sonda.
/// </summary>
public static class ObjectiveBuildProbe
{
    public static async Task<ObjectiveBuildProbeResult> RunAsync(
        string worktreePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);

        var surfaces = FindSurfaces(worktreePath);
        if (surfaces.Length == 0)
        {
            return new ObjectiveBuildProbeResult(
                false, true, "Sem superfície .NET (.sln ou src/*/*.csproj) — sonda não aplicável.");
        }

        foreach (var surface in surfaces)
        {
            var (exitCode, output) = await ExecuteAsync(
                worktreePath,
                ["build", surface, "-v", "q", "--nologo"],
                timeout,
                cancellationToken);
            if (exitCode != 0)
            {
                var tail = Tail(output, 1_200);
                return new ObjectiveBuildProbeResult(
                    true, false,
                    $"`dotnet build {Path.GetFileName(surface)}` saiu {exitCode}. Saída:\n{tail}");
            }
        }

        return new ObjectiveBuildProbeResult(
            true, true,
            $"Compilou: {string.Join(", ", surfaces.Select(Path.GetFileName))}.");
    }

    private static string[] FindSurfaces(string root)
    {
        var solutions = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.Exists(Path.Combine(root, "src"))
                ? Directory.EnumerateFiles(Path.Combine(root, "src"), "*.sln", SearchOption.TopDirectoryOnly)
                : [])
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (solutions.Length > 0)
        {
            return [solutions[0]];
        }

        var src = Path.Combine(root, "src");
        if (!Directory.Exists(src))
        {
            return [];
        }

        return Directory.EnumerateDirectories(src)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    private static async Task<(int ExitCode, string Output)> ExecuteAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var buffer = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) buffer.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) buffer.AppendLine(e.Data); };
        _ = process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (124, buffer.ToString() + $"\n(sonda de build excedeu {timeout.TotalMinutes:0} min)");
        }

        return (process.ExitCode, buffer.ToString());
    }

    private static string Tail(string value, int size) =>
        value.Length <= size ? value : value[^size..];
}
