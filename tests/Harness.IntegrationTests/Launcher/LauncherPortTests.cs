using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Harness.Launcher;

namespace Harness.IntegrationTests.Launcher;

public sealed class LauncherPortTests
{
    [Fact]
    public void ExplicitPortWinsAndIsNeverPersisted()
    {
        using var scope = new TempDirectory();
        var resolved = LauncherPort.Resolve(scope.Path, 6123);
        Assert.Equal(6123, resolved);
        Assert.False(File.Exists(LauncherPort.PortFilePath(scope.Path)));
    }

    [Fact]
    public void FirstStartPersistsChosenPortAndReusesItThereafter()
    {
        using var scope = new TempDirectory();
        var first = LauncherPort.Resolve(scope.Path, null);
        Assert.InRange(first, 1, 65535);

        var portFile = LauncherPort.PortFilePath(scope.Path);
        Assert.True(File.Exists(portFile));
        Assert.Equal(first, int.Parse(File.ReadAllText(portFile).Trim(), CultureInfo.InvariantCulture));

        // Segundo start (porta ainda livre): reusa exatamente a persistida — bookmark estável.
        var second = LauncherPort.Resolve(scope.Path, null);
        Assert.Equal(first, second);
    }

    [Fact]
    public void PersistedButOccupiedPortFallsBackWithoutOverwritingThePreference()
    {
        using var scope = new TempDirectory();
        using var occupied = new OccupiedPort();
        var portFile = LauncherPort.PortFilePath(scope.Path);
        File.WriteAllText(portFile, occupied.Port.ToString(CultureInfo.InvariantCulture));

        var resolved = LauncherPort.Resolve(scope.Path, null);

        Assert.NotEqual(occupied.Port, resolved);
        Assert.True(LauncherPort.IsFree(resolved));
        // A preferência salva NÃO muda: quando a porta preferida liberar, o start volta a ela.
        Assert.Equal(
            occupied.Port,
            int.Parse(File.ReadAllText(portFile).Trim(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void IsFreeReflectsPortAvailability()
    {
        using var occupied = new OccupiedPort();
        Assert.False(LauncherPort.IsFree(occupied.Port));

        var free = LauncherPort.FindFreePort(occupied.Port);
        Assert.NotEqual(0, free);
        Assert.NotEqual(occupied.Port, free);
        Assert.True(LauncherPort.IsFree(free));
    }

    private sealed class OccupiedPort : IDisposable
    {
        private readonly TcpListener _listener;

        public OccupiedPort()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public void Dispose() => _listener.Stop();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "integration-artifacts",
                $"port-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
