using System;
using System.Collections.Generic;
using System.IO;

namespace Harness.Bruna.Desktop;

public sealed record BrunaOptions
{
    public string DataDirectory { get; init; } = DefaultDataDirectory();

    public string? InstallDirectory { get; init; }

    public static BrunaOptions Parse(IReadOnlyList<string> args)
    {
        string? dataDirectory = null;
        string? installDirectory = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--data-dir" when index + 1 < args.Count:
                    dataDirectory = args[++index];
                    break;
                case "--install-dir" when index + 1 < args.Count:
                    installDirectory = args[++index];
                    break;
                default:
                    throw new ArgumentException(
                        $"Argumento desconhecido: {args[index]}. Use --data-dir <caminho> ou --install-dir <caminho>.");
            }
        }

        var resolved = dataDirectory ?? DefaultDataDirectory();
        if (!Path.IsPathFullyQualified(resolved))
        {
            resolved = Path.GetFullPath(resolved);
        }

        if (installDirectory is not null && !Path.IsPathFullyQualified(installDirectory))
        {
            installDirectory = Path.GetFullPath(installDirectory);
        }

        return new BrunaOptions
        {
            DataDirectory = resolved,
            InstallDirectory = installDirectory,
        };
    }

    public static string DefaultDataDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".harness-poseidon");
}
