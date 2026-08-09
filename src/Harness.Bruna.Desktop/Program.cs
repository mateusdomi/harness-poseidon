using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Harness.Bruna.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = BrunaOptions.Parse(args);

        using var singleInstance = SingleInstanceGuard.Acquire(options.DataDirectory);
        if (singleInstance is null)
        {
            Console.Error.WriteLine("Bruna Desktop Companion já está em execução.");
            return 0;
        }

        var config = BrunaConfiguration.Load(options.DataDirectory);
        config.InstallDirectory = options.InstallDirectory ?? ResolveInstallDirectory();

        return BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    public static string? ResolveInstallDirectoryStatic()
        => ResolveInstallDirectory();

    private static string? ResolveInstallDirectory()
    {
        var executableDirectory = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(executableDirectory, "Harness.Launcher")) ||
            File.Exists(Path.Combine(executableDirectory, "Harness.Launcher.exe")))
        {
            return executableDirectory;
        }

        for (var directory = new DirectoryInfo(executableDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harness.Launcher")) ||
                File.Exists(Path.Combine(directory.FullName, "Harness.Launcher.exe")))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}
