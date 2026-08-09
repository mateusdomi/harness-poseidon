using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Harness.Bruna.Desktop;

public sealed partial class BrunaMascotWindow : Window, IDisposable
{
    private const double WindowSize = 160;
    private const double ClipWidth = 128;
    private const double ClipHeight = 152;
    private const double ClipMarginX = (WindowSize - ClipWidth) / 2.0;
    private const double ClipMarginY = 4;

    private readonly BrunaConfiguration _configuration;
    private readonly PoseidonDiscovery _discovery;
    private readonly PoseidonLauncher _launcher;
    private readonly BrunaStateManager _stateManager;
    private readonly BrunaPhotoProcessor _photoProcessor;
    private readonly DispatcherTimer _animationTimer = new();
    private readonly ScaleTransform _scaleTransform = new();
    private readonly RotateTransform _rotateTransform = new();

    private CancellationTokenSource? _startupCts;
    private bool _disposed;

    public BrunaMascotWindow(
        BrunaConfiguration configuration,
        PoseidonDiscovery discovery,
        PoseidonLauncher launcher,
        BrunaStateManager stateManager)
    {
        InitializeComponent();
        _configuration = configuration;
        _discovery = discovery;
        _launcher = launcher;
        _stateManager = stateManager;
        _photoProcessor = new BrunaPhotoProcessor(configuration.FilePath is { Length: > 0 } filepath
            ? Path.GetDirectoryName(Path.GetDirectoryName(filepath))!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harness-poseidon"));

        RestorePosition();
        ApplyDpiScaling();
        SetupContextMenu();
        SetupAnimations();

        _stateManager.StateChanged += OnStateChanged;
        OnStateChanged(this, _stateManager.State);

        PositionChanged += OnPositionChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _startupCts?.Cancel();
        _startupCts?.Dispose();
        _animationTimer.Stop();
        _stateManager.StateChanged -= OnStateChanged;
        _stateManager.Dispose();
        _discovery.Dispose();
        SaveConfiguration();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Dispose();
        base.OnClosing(e);
    }

    private void RestorePosition()
    {
        var screen = Screens.ScreenFromPoint(new PixelPoint((int)_configuration.X, (int)_configuration.Y))
                     ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var workingArea = screen.WorkingArea;
        var x = Math.Max(workingArea.X, Math.Min(workingArea.Right - (int)WindowSize, (int)_configuration.X));
        var y = Math.Max(workingArea.Y, Math.Min(workingArea.Bottom - (int)WindowSize, (int)_configuration.Y));
        Position = new PixelPoint(x, y);
    }

    private void ApplyDpiScaling()
    {
        var processedPhoto = _photoProcessor.TryGetProcessedPhoto();
        if (!string.IsNullOrEmpty(processedPhoto))
        {
            LoadCustomPhoto(processedPhoto, hasTransparentBackground: true);
            return;
        }

        // Ainda não há foto processada em cache; usa o asset embutido enquanto o
        // processamento da foto customizada (se houver) acontece em background.
        var customPhoto = _configuration.ResolveCustomPhotoPath();
        if (!string.IsNullOrEmpty(customPhoto))
        {
            LoadCustomPhoto(customPhoto, hasTransparentBackground: false);
            return;
        }

        var scaling = Screens.ScreenFromWindow(this)?.Scaling ?? 1.0;
        var assetName = scaling >= 1.5
            ? "avares://Harness.Bruna.Desktop/Assets/bruna-idle@2x.png"
            : "avares://Harness.Bruna.Desktop/Assets/bruna-idle.png";

        try
        {
            var uri = new Uri(assetName);
            using var stream = AssetLoader.Open(uri);
            BrunaImage.Source = new Bitmap(stream);
            BrunaImage.Clip = null;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Falha ao carregar asset: {exception.Message}");
        }
    }

    private void LoadCustomPhoto(string path, bool hasTransparentBackground)
    {
        try
        {
            using var stream = File.OpenRead(path);
            BrunaImage.Source = new Bitmap(stream);
            // Elipse vertical para acomodar fotos de retrato sem cortar a cabeça.
            BrunaImage.Clip = new EllipseGeometry(new Rect(
                ClipMarginX, ClipMarginY, ClipWidth, ClipHeight));
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Falha ao carregar foto customizada: {exception.Message}");
            ApplyDpiScaling();
        }
    }

    private void SetupContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("Abrir Poseidon", OnOpenPoseidon));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Mostrar/Ocultar Bruna", OnToggleVisibility));
        menu.Items.Add(CreateMenuItem("Reposicionar", OnReposition));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Sobre o Poseidon", OnAbout));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Sair", OnExit));
        RootPanel.ContextMenu = menu;
    }

    private static MenuItem CreateMenuItem(string header, EventHandler<RoutedEventArgs> handler)
    {
        var item = new MenuItem { Header = header };
        item.Click += handler;
        return item;
    }

    private void SetupAnimations()
    {
        // 30 fps é suficiente para microanimações discretas e reduz carga na UI thread.
        _animationTimer.Interval = TimeSpan.FromMilliseconds(33);

        var transform = new TransformGroup();
        transform.Children.Add(_scaleTransform);
        transform.Children.Add(_rotateTransform);
        BrunaImage.RenderTransform = transform;
        BrunaImage.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);

        var startTime = DateTimeOffset.UtcNow;
        _animationTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
            Animate(elapsed);
        };
        _animationTimer.Start();
    }

    private void Animate(double elapsed)
    {
        var state = _stateManager.State;
        var scale = state switch
        {
            BrunaVisualState.Idle => 1.0 + Math.Sin(elapsed * 1.5) * 0.015,
            BrunaVisualState.Starting => 1.0 + Math.Sin(elapsed * 8.0) * 0.04,
            BrunaVisualState.Working => 1.0 + Math.Sin(elapsed * 6.0) * 0.03,
            BrunaVisualState.Waiting => 1.0 + Math.Sin(elapsed * 2.0) * 0.02,
            BrunaVisualState.Error => 1.0,
            _ => 1.0,
        };

        var rotate = state switch
        {
            BrunaVisualState.Starting => Math.Sin(elapsed * 6.0) * 3.0,
            BrunaVisualState.Working => Math.Sin(elapsed * 4.0) * 2.0,
            _ => 0.0,
        };

        _scaleTransform.ScaleX = scale;
        _scaleTransform.ScaleY = scale;
        _rotateTransform.Angle = rotate;
    }

    private void OnStateChanged(object? sender, BrunaVisualState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusIndicator.Fill = state switch
            {
                BrunaVisualState.Idle => Brushes.MediumSeaGreen,
                BrunaVisualState.Starting => Brushes.Gold,
                BrunaVisualState.Working => Brushes.DodgerBlue,
                BrunaVisualState.Waiting => Brushes.Orange,
                BrunaVisualState.Error => Brushes.Crimson,
                _ => Brushes.Gray,
            };
        });
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // BeginMoveDrag move a janela nativamente; nao misturamos com atualizacao
            // manual de Position para evitar travamentos/lag no arraste.
            BeginMoveDrag(e);
        }
    }

    private void OnPositionChanged(object? sender, EventArgs e)
    {
        _configuration.X = Position.X;
        _configuration.Y = Position.Y;
        SaveConfiguration();
    }

    public void OpenPoseidon()
    {
        _ = OpenPoseidonAsync();
    }

    private void OnOpenPoseidon(object? sender, RoutedEventArgs e)
    {
        OpenPoseidon();
    }

    private async Task OpenPoseidonAsync()
    {
        _stateManager.SetUserInteractionState(BrunaVisualState.Working);
        _startupCts?.Cancel();
        _startupCts?.Dispose();
        _startupCts = new CancellationTokenSource();

        try
        {
            var instance = await _discovery.ProbeAsync(_startupCts.Token).ConfigureAwait(false);
            if (instance.Status == PoseidonStatus.Healthy && instance.Address is not null)
            {
                OpenBrowser(instance.Address);
                return;
            }

            _stateManager.SetUserInteractionState(BrunaVisualState.Starting);
            var result = await _launcher.StartAsync(_startupCts.Token).ConfigureAwait(false);
            if (result.Success && result.Address is not null)
            {
                OpenBrowser(result.Address);
            }
            else
            {
                _stateManager.SetUserInteractionState(BrunaVisualState.Error);
            }
        }
        catch (OperationCanceledException)
        {
            _stateManager.SetUserInteractionState(BrunaVisualState.Waiting);
        }
        finally
        {
            _stateManager.ResetToObservedState();
        }
    }

    private static void OpenBrowser(Uri address)
    {
        try
        {
            var startInfo = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("/usr/bin/open", address.ToString())
                : OperatingSystem.IsWindows()
                    ? new ProcessStartInfo("cmd", $"/c start {address}")
                    : new ProcessStartInfo("xdg-open", address.ToString());
            startInfo.UseShellExecute = false;
            using var process = Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Falha ao abrir navegador: {exception.Message}");
        }
    }

    private void OnToggleVisibility(object? sender, RoutedEventArgs e)
    {
        if (IsVisible)
        {
            Hide();
            _configuration.Visible = false;
        }
        else
        {
            Show();
            _configuration.Visible = true;
        }

        SaveConfiguration();
    }

    private void OnReposition(object? sender, RoutedEventArgs e)
    {
        var screen = Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var workingArea = screen.WorkingArea;
        Position = new PixelPoint(
            workingArea.X + workingArea.Width - (int)WindowSize - 20,
            workingArea.Y + workingArea.Height - (int)WindowSize - 20);
        _configuration.X = Position.X;
        _configuration.Y = Position.Y;
        SaveConfiguration();
    }

    private void OnAbout(object? sender, RoutedEventArgs e)
    {
        var installDir = _configuration.InstallDirectory ?? "desconhecido";
        var message = $"Bruna Desktop Companion para Poseidon.\nInstalação: {installDir}";
        var dialog = new Window
        {
            Title = "Sobre",
            Width = 360,
            Height = 160,
            Content = new TextBlock
            {
                Text = message,
                Margin = new Thickness(20),
                TextWrapping = TextWrapping.Wrap,
            },
        };
        dialog.ShowDialog(this);
    }

    private void OnExit(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SaveConfiguration()
    {
        try
        {
            _configuration.Save();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Falha ao salvar configuração: {exception.Message}");
        }
    }
}
