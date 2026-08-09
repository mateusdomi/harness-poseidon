using System;
using System.IO;
using Xunit;

namespace Harness.Bruna.Desktop.Tests;

public sealed class SingleInstanceGuardTests : IDisposable
{
    private readonly string _dataDirectory;

    public SingleInstanceGuardTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"bruna-single-{Guid.NewGuid():N}");
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
    public void AcquireRetornaGuardaQuandoNaoEstaExecutando()
    {
        using var guard = SingleInstanceGuard.Acquire(_dataDirectory);

        Assert.NotNull(guard);
    }

    [Fact]
    public void AcquireRetornaNuloQuandoJaEstaExecutando()
    {
        using var first = SingleInstanceGuard.Acquire(_dataDirectory);
        Assert.NotNull(first);

        var second = SingleInstanceGuard.Acquire(_dataDirectory);
        Assert.Null(second);
    }

    [Fact]
    public void AcquireReadquireAposDispose()
    {
        var first = SingleInstanceGuard.Acquire(_dataDirectory);
        first?.Dispose();

        using var second = SingleInstanceGuard.Acquire(_dataDirectory);

        Assert.NotNull(second);
    }
}
