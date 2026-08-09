using System;
using System.IO;
using Xunit;

namespace Harness.Bruna.Desktop.Tests;

public sealed class BrunaOptionsTests
{
    [Fact]
    public void ParseUsaDiretorioDeDadosPadrao()
    {
        var options = BrunaOptions.Parse([]);

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".harness-poseidon");
        Assert.Equal(expected, options.DataDirectory);
    }

    [Fact]
    public void ParseResolveDiretorioDeDadosEmCaminhoCompleto()
    {
        var options = BrunaOptions.Parse(["--data-dir", "./bruna-data"]);

        Assert.True(Path.IsPathFullyQualified(options.DataDirectory));
        Assert.EndsWith("bruna-data", options.DataDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseAceitaDiretorioDeInstalacao()
    {
        var options = BrunaOptions.Parse(["--install-dir", "/opt/poseidon"]);

        Assert.Equal("/opt/poseidon", options.InstallDirectory);
    }

    [Fact]
    public void ParseLancaParaArgumentoDesconhecido()
    {
        Assert.Throws<ArgumentException>(() => BrunaOptions.Parse(["--unknown", "value"]));
    }
}
