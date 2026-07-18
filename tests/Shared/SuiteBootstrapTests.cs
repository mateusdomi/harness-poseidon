using System.Reflection;
using System.Runtime.Versioning;

namespace Harness.Tests.Bootstrap;

public sealed class SuiteBootstrapTests
{
    [Fact]
    public void TestSuiteTargetsNet10()
    {
        var targetFramework = typeof(SuiteBootstrapTests)
            .Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?
            .FrameworkName;

        Assert.Equal(".NETCoreApp,Version=v10.0", targetFramework);
    }
}
