using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Harness.ArchitectureTests;

public sealed class DependencyDirectionTests
{
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(typeof(SharedKernel.SharedKernelAssembly).Assembly, typeof(Program).Assembly)
        .Build();

    [Fact]
    public void SharedKernelDoesNotDependOnHost()
    {
        var sharedKernel = Types()
            .That()
            .ResideInAssembly(typeof(SharedKernel.SharedKernelAssembly).Assembly);
        var host = Types()
            .That()
            .ResideInAssembly(typeof(Program).Assembly);

        Types()
            .That()
            .Are(sharedKernel)
            .Should()
            .NotDependOnAny(host)
            .Check(Architecture);
    }
}
