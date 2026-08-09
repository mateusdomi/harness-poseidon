using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Harness.Bruna.Desktop.Tests;

public sealed class BrunaConfigurationTests : IDisposable
{
    private readonly string _dataDirectory;

    public BrunaConfigurationTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"bruna-tests-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void LoadCriaConfiguracaoPadraoQuandoArquivoNaoExiste()
    {
        var config = BrunaConfiguration.Load(_dataDirectory);

        Assert.True(config.Visible);
        Assert.Equal(100, config.X);
        Assert.Equal(100, config.Y);
    }

    [Fact]
    public void SavePersisteConfiguracao()
    {
        var config = BrunaConfiguration.Load(_dataDirectory);
        config.X = 123;
        config.Y = 456;
        config.Visible = false;
        config.InstallDirectory = "/opt/poseidon";

        config.Save();
        var loaded = BrunaConfiguration.Load(_dataDirectory);

        Assert.Equal(123, loaded.X);
        Assert.Equal(456, loaded.Y);
        Assert.False(loaded.Visible);
        Assert.Equal("/opt/poseidon", loaded.InstallDirectory);
    }

    [Fact]
    public void LoadIgnoraArquivoCorrompidoERetornaPadrao()
    {
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "bruna"));
        var path = Path.Combine(_dataDirectory, "bruna", "config.json");
        File.WriteAllText(path, "not-json");

        var config = BrunaConfiguration.Load(_dataDirectory);

        Assert.Equal(100, config.X);
    }

    [Fact]
    public void SaveEscreveArquivoAtomicamente()
    {
        var config = BrunaConfiguration.Load(_dataDirectory);
        config.X = 42;

        config.Save();

        var path = Path.Combine(_dataDirectory, "bruna", "config.json");
        Assert.True(File.Exists(path));
        var json = File.ReadAllText(path);
        Assert.Contains("42", json, StringComparison.Ordinal);
    }
}
