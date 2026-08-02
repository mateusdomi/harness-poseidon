namespace Harness.Modules.Operations;

/// <summary>Classe de custo de uma operação no host.</summary>
public enum ResourceClass
{
    /// <summary>Leitura, grep, análise, edição, diff. Paraleliza à vontade.</summary>
    Light,

    /// <summary>Teste pontual, compilação pequena, navegador leve.</summary>
    Medium,

    /// <summary>Build completo, suíte completa, Playwright, Docker build, indexação.</summary>
    Heavy,

    /// <summary>verify completo, E2E 1-9, migração destrutiva, re-piloto, benchmark oficial.</summary>
    Exclusive,
}

/// <summary>Sinais de pressão do host. RAM livre NÃO está aqui, de propósito.</summary>
public sealed record HostPressure(
    /// <summary>Saída de `memory_pressure`, quando disponível: normal | warn | critical.</summary>
    string? MemoryPressureLevel,
    long SwapUsedBytes,
    long PageoutDelta,
    int HeavyRunning,
    int ExclusiveRunning);

/// <summary>
/// Decide se uma operação pode começar agora.
///
/// A correção importante aqui é o que NÃO se mede. Concluir "só cabem dois agentes" a
/// partir de "211 MB livres" é ler mal o macOS: ele usa a RAM disponível agressivamente
/// como cache, e RAM livre baixa com swap zerado é um sistema saudável, não um sistema
/// afogado. Os sinais que importam são pressão de memória, swap e pageouts.
///
/// O episódio dos 35 GB não prova que sete agentes são inviáveis — prova que várias
/// operações PESADAS simultâneas são perigosas. Por isso o teto é por classe: agentes
/// leves podem andar em bando; build completo, não.
/// </summary>
public static class HostResourcePolicy
{
    /// <summary>Concorrência inicial de HEAVY. Sobe para 2 só com folga medida.</summary>
    public const int DefaultHeavyLimit = 1;

    /// <summary>Operações exclusivas nunca compartilham o host.</summary>
    public const int ExclusiveLimit = 1;

    /// <summary>Agentes leves simultâneos com o host saudável.</summary>
    public const int LightAgentLimit = 4;

    public static bool CanStart(ResourceClass resource, HostPressure pressure, int heavyLimit = DefaultHeavyLimit)
    {
        ArgumentNullException.ThrowIfNull(pressure);

        if (pressure.ExclusiveRunning > 0)
        {
            return false;
        }

        return resource switch
        {
            ResourceClass.Exclusive => pressure.HeavyRunning == 0,
            ResourceClass.Heavy => pressure.HeavyRunning < heavyLimit && !IsCritical(pressure),
            ResourceClass.Medium => !IsCritical(pressure),
            ResourceClass.Light => true,
            _ => false,
        };
    }

    /// <summary>
    /// Pressão crítica: o kernel já está trabalhando para achar memória. Swap em uso e
    /// pageouts são a evidência disso; "pages free" baixo, sozinho, não é.
    /// </summary>
    public static bool IsCritical(HostPressure pressure)
    {
        ArgumentNullException.ThrowIfNull(pressure);
        if (string.Equals(pressure.MemoryPressureLevel, "critical", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return pressure.SwapUsedBytes > 0 && pressure.PageoutDelta > 0;
    }

    /// <summary>
    /// Sob pressão não se mata trabalho válido: para-se de ADMITIR trabalho novo e espera-se
    /// o que está em voo terminar. Matar um build pela metade só troca uma pressão por um
    /// retrabalho.
    /// </summary>
    public static bool ShouldAdmitNewWork(HostPressure pressure) => !IsCritical(pressure);
}
