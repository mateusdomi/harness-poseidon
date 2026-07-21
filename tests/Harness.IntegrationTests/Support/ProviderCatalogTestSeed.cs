using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Support;

/// <summary>
/// Semeia o catálogo SIMULADO de providers/modelos/cotas para fixtures que precisam de um
/// catálogo utilizável (ADR-018). Em produção esse catálogo só existe sob demonstração
/// explícita; os testes o pedem deliberadamente, sempre rotulado como simulado, em vez de
/// depender de um seed automático que apresentaria dados fictícios como reais.
/// </summary>
public static class ProviderCatalogTestSeed
{
    /// <summary>Semeia o catálogo simulado para um tenant explícito.</summary>
    public static Task SeedAsync(
        IServiceProvider services,
        string tenantId,
        CancellationToken cancellationToken) =>
        services.GetRequiredService<IProviderCatalogStore>()
            .SeedSimulatedCatalogAsync(tenantId, cancellationToken);

    /// <summary>
    /// Semeia o catálogo simulado para o tenant do primeiro perfil local existente. Use após
    /// criar o perfil da fixture.
    /// </summary>
    public static async Task SeedForLocalProfileAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var profiles = await services.GetRequiredService<ILocalProfileStore>()
            .ListAsync(cancellationToken);
        if (profiles.Count == 0)
        {
            throw new InvalidOperationException(
                "Nenhum perfil local existe; crie o perfil da fixture antes de semear o catálogo.");
        }

        await SeedAsync(services, profiles[0].TenantId, cancellationToken);
    }
}
