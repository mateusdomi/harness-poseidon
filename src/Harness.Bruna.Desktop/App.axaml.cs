using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace Harness.Bruna.Desktop;

public partial class App : Application, IDisposable
{
    private TrayIcon? _trayIcon;
    private bool _disposed;

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

            var window = new BrunaMascotWindow(config, discovery, launcher, stateManager);
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            SetupTrayIcon(window);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _trayIcon?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SetupTrayIcon(BrunaMascotWindow window)
    {
        try
        {
            var iconUri = new Uri("avares://Harness.Bruna.Desktop/Assets/bruna-thumbnail.png");
            using var iconStream = AssetLoader.Open(iconUri);
            var icon = new WindowIcon(iconStream);

            var showItem = new NativeMenuItem("Mostrar Bruna");
            showItem.Click += (_, _) =>
            {
                window.Show();
                window.Activate();
            };

            var openItem = new NativeMenuItem("Abrir Poseidon");
            openItem.Click += (_, _) => window.OpenPoseidon();

            var exitItem = new NativeMenuItem("Sair");
            exitItem.Click += (_, _) => window.Close();

            var menu = new NativeMenu();
            menu.Items.Add(showItem);
            menu.Items.Add(openItem);
            menu.Items.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "Bruna Desktop Companion",
                Menu = menu,
            };
            _trayIcon.Clicked += (_, _) =>
            {
                window.Show();
                window.Activate();
            };
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Falha ao criar tray icon: {exception.Message}");
        }
    }
}
