namespace Harness.IntegrationTests.Product;

/// <summary>
/// Os testes que EXECUTAM processo de verdade (`dotnet build`, `dotnet test`, `npm run …`) rodam
/// serializados entre si. Em paralelo eles saturam a CPU da máquina e derrubam por tempo testes
/// vizinhos que nada têm a ver com eles — foi o que aconteceu com o pipeline de dogfood.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessVerificationGroup
{
    public const string Name = "process-verification";
}
