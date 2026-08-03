using Harness.Host.Agents;
using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// D7 — a chave de assento do Conselho é CONTRATO com o catálogo de personas.
///
/// O defeito que estes testes fecham não derrubava nada: ele trocava a lente em silêncio. A
/// resolução de persona não falha diante de uma chave desconhecida — cai no fallback de persona
/// inferida —, então um assento com chave errada continua executando, produz parecer, fecha o
/// card e conta para o quórum. O conselho aparenta seis lentes e tem cinco.
///
/// Medido em 03/08/2026 na prova limpa: <c>playbook-po</c> não existe no catálogo
/// (<c>playbook-product-owner</c> existe), e o assento de Produto foi executado pela persona
/// Software Architect. Numa mesa que já tinha um Arquiteto, a lente perdida foi exatamente a
/// única que pergunta se o escopo entrega valor em vez de atividade — a pergunta que nenhuma
/// outra lente faz.
///
/// Por isso a verificação é do conjunto INTEIRO e não de uma chave: a lista de assentos e a lista
/// de personas moram em módulos diferentes (Coordination e Host) e evoluem por mãos diferentes.
/// Enquanto nada as ligasse, a próxima divergência era só questão de tempo.
/// </summary>
public sealed class CouncilSeatCatalogContractTests
{
    private static readonly HashSet<string> CatalogKeys =
        [.. CanonicalAgentDefinitions.All.Select(seed => seed.Content.Key)];

    public static TheoryData<string> AllSeatKeys()
    {
        var data = new TheoryData<string>();
        foreach (var seat in AgentCouncilPolicy.Seats)
        {
            data.Add(seat.PersonaKey);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllSeatKeys))]
    public void TodaChaveDeAssentoExisteNoCatalogoDePersonas(string personaKey)
    {
        Assert.True(
            CatalogKeys.Contains(personaKey),
            $"O assento do Conselho declara a persona '{personaKey}', que NÃO existe em " +
            $"CanonicalAgentDefinitions. O assento não falharia: cairia no fallback de persona " +
            $"inferida e produziria parecer com outra lente, contando para o quórum como se " +
            $"fosse a lente declarada. Chaves disponíveis com prefixo 'playbook-': " +
            string.Join(", ", CatalogKeys.Where(key =>
                key.StartsWith("playbook-", StringComparison.Ordinal)).Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// Duas lentes com a mesma persona são uma lente contada duas vezes. O piso de
    /// <see cref="AgentCouncilPolicy.MinimumCouncil"/> existe para que uma divergência isolada
    /// apareça como divergência; ele não protege nada se dois assentos forem o mesmo profissional.
    /// </summary>
    [Fact]
    public void NenhumaPersonaOcupaDoisAssentos()
    {
        var duplicated = AgentCouncilPolicy.Seats
            .GroupBy(seat => seat.PersonaKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.True(
            duplicated.Length == 0,
            $"Personas ocupando mais de um assento: {string.Join(", ", duplicated)}.");
    }

    /// <summary>
    /// A lente é o que distingue um assento do outro. Duas perguntas idênticas em cadeiras
    /// diferentes produzem a mesma cegueira duas vezes e dão a ela aparência de consenso — que é
    /// exatamente o que o comentário de <see cref="AgentCouncilPolicy"/> diz querer evitar.
    /// </summary>
    [Fact]
    public void CadaAssentoFazUmaPerguntaQueNenhumOutroFaz()
    {
        var lenses = AgentCouncilPolicy.Seats.Select(seat => seat.Lens).ToArray();

        Assert.Equal(lenses.Length, lenses.Distinct(StringComparer.Ordinal).Count());
        Assert.All(lenses, lens => Assert.False(string.IsNullOrWhiteSpace(lens)));
    }

    /// <summary>
    /// O núcleo é o que sobra quando o projeto não justifica nenhum assento condicional. Ele
    /// precisa, sozinho, satisfazer o piso — senão a mesa mínima nasce inválida e o conselho
    /// devolve <c>council.incomplete</c> em todo projeto sem marcas de risco declaradas.
    /// </summary>
    [Fact]
    public void ONucleoSozinhoJaSatisfazOPiso()
    {
        Assert.True(AgentCouncilPolicy.CoreSeats.Count >= AgentCouncilPolicy.MinimumCouncil);
    }
}
