using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Harness.Bruna.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = Environment.GetCommandLineArgs();
            var options = BrunaOptions.Parse(args[1..]);
            var config = BrunaConfiguration.Load(options.DataDirectory);
            config.InstallDirectory = options.InstallDirectory
                ?? Program.ResolveInstallDirectoryStatic()
                ?? config.InstallDirectory;

            var discovery = new PoseidonDiscovery(config);
            var launcher = new PoseidonLauncher(config);
            var stateManager = new BrunaStateManager(discovery);

            desktop.MainWindow = new BrunaMascotWindow(config, discovery, launcher, stateManager);
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
