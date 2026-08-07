using Harness.SharedKernel.CodeGraph;

namespace Harness.Modules.Coordination.Application;

/// <summary>
/// Uma divergência entre o plano e o grafo. <paramref name="Evidence"/> carrega os nós concretos que
/// a produziram — uma divergência sem evidência é indistinguível de opinião, e ninguém deveria
/// bloquear despacho por opinião.
/// </summary>
public sealed record PlanGraphDivergence(
    string Code,
    string CardId,
    string? RelatedCardId,
    IReadOnlyList<string> Evidence,
    string Explanation);

public sealed record PlanGraphValidation(
    bool DispatchAllowed,
    string ReasonCode,
    IReadOnlyList<PlanGraphDivergence> Blocking,
    IReadOnlyList<PlanGraphDivergence> Advisory,
    IReadOnlyList<string> ValidatedCardIds,
    IReadOnlyList<string> UnverifiableCardIds)
{
    public IReadOnlyList<PlanGraphDivergence> All => [.. Blocking, .. Advisory];
}

/// <summary>
/// VALIDAÇÃO DO PLANO CONTRA O GRAFO (B6).
///
/// A Bruna declara as dependências entre cards ao decompor a demanda. Essa declaração é uma
/// PREVISÃO, e até aqui nada no sistema conseguia conferi-la: o plano era despachado com a ordem que
/// ele mesmo afirmava estar certa. Quando a previsão errava, o sintoma não aparecia como erro de
/// plano — aparecia como conflito de merge, teste vermelho intermitente ou card refeito, horas
/// depois, longe da causa.
///
/// Com o grafo de código a conferência passa a ser possível: se o código que o card B vai tocar
/// depende do código que o card A vai tocar, então B depende de A — independentemente do que o plano
/// disse. A divergência entre o calculado e o declarado é, por isso, defeito de PLANO, e o momento
/// certo de tratá-la é antes do despacho.
///
/// <b>As três divergências não têm o mesmo peso, e é deliberado:</b>
///
/// <list type="number">
///   <item><b>Dependência não declarada</b> (o grafo tem, o plano não) — <b>BLOQUEIA</b>. É
///     demonstrável: existe uma aresta de código entre os escopos. Despachar em paralelo dois cards
///     assim é agendar o conflito.</item>
///   <item><b>Escopo compartilhado não declarado</b> (dois cards tocam o MESMO nó sem nenhuma ordem
///     entre eles) — <b>BLOQUEIA</b>. Não é dependência, é colisão: dois agentes editando o mesmo
///     tipo ao mesmo tempo.</item>
///   <item><b>Declarada sem evidência no código</b> (o plano tem, o grafo não) — <b>NÃO bloqueia</b>.
///     O grafo vê acoplamento, não intenção: uma ordem pode existir por regra de negócio, por
///     sequência de aprovação ou porque o segundo card só faz sentido depois do primeiro. Bloquear
///     aqui seria o grafo se declarar mais sabido que o planejador sobre algo que ele não enxerga.
///     Fica registrada como observação.</item>
/// </list>
///
/// <b>E o que o índice não cobre.</b> Card cujos caminhos não estão no índice sai em
/// <see cref="PlanGraphValidation.UnverifiableCardIds"/>: não bloqueia (senão a fase que ainda não
/// indexou o frontend paralisaria a campanha inteira) e JAMAIS entra em
/// <see cref="PlanGraphValidation.ValidatedCardIds"/>. Não conferido e conferido não podem sair pela
/// mesma porta — foi exatamente assim que o sistema aprendeu a chamar "verificado" o que ninguém
/// olhou.
/// </summary>
public static class PlanGraphValidationPolicy
{
    public const string CodeUndeclaredDependency = "plan_graph.undeclared_dependency";
    public const string CodeSharedScope = "plan_graph.shared_scope_undeclared";
    public const string CodeDeclaredWithoutEvidence = "plan_graph.declared_without_code_evidence";

    public const string ReasonConsistent = "plan_graph.consistent";
    public const string ReasonBlocked = "plan_graph.divergence_blocks_dispatch";

    /// <summary>
    /// Confere o plano declarado contra o grafo. <paramref name="declaredEdges"/> vem do
    /// <see cref="CardDependencyGraph"/> (provedor → consumidor), e é a previsão sob teste. Os
    /// escopos são os MESMOS <see cref="CardScope"/> que a F14 já usa para montar a fronteira
    /// negativa do card — o que o card possui é o que ele vai tocar, e duas definições disso no
    /// mesmo sistema divergiriam.
    ///
    /// <paramref name="unnarrowedCardIds"/> — recuperação de throughput da Fase 5 (2026-08-07):
    /// quando <c>CardPathScopePlanner</c> não consegue estreitar o escopo de um card (nenhuma
    /// superfície reconhecida — comum em cards conceituais/cross-cutting como os artefatos de
    /// suporte da fase e correções amplas de DoD), ele devolve o escopo INTEIRO do papel como
    /// fallback conservador — por exemplo `src/**` inteiro. Medido ao vivo: quatro cards desse
    /// tipo no mesmo projeto, todos com o mesmo fallback, bloqueavam-se mutuamente aos pares
    /// (seis colisões de um só grupo) — o backlog inteiro travava em `0 despachado(s)` por
    /// ciclo, com contas livres e nada rodando. Dois fallbacks idênticos não são evidência de que
    /// os cards vão tocar o mesmo arquivo; são evidência de que o planejador não sabia de nenhum
    /// dos dois. Um card com escopo REAL (estreitado) contra um fallback amplo continua
    /// bloqueando — o fallback pode genuinamente tocar o arquivo estreito do outro, e aí a
    /// colisão é real. A segurança não afrouxa: o ScopeClaim real, na aquisição da tentativa,
    /// continua sendo a segunda camada que pega qualquer sobreposição de verdade neste ou em
    /// qualquer outro par — este ajuste só evita o pré-bloqueio de um par sobre o qual ninguém
    /// tem informação nenhuma.
    /// </summary>
    public static PlanGraphValidation Validate(
        IReadOnlyList<CardScope> scopes,
        IReadOnlyList<CardDependencyEdge> declaredEdges,
        CodeGraph graph,
        IReadOnlySet<string>? unnarrowedCardIds = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(declaredEdges);
        ArgumentNullException.ThrowIfNull(graph);
        var unnarrowed = unnarrowedCardIds ?? new HashSet<string>(StringComparer.Ordinal);

        var touchedByCard = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var unverifiable = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            ArgumentNullException.ThrowIfNull(scope);
            ArgumentException.ThrowIfNullOrWhiteSpace(scope.CardId);
            var radius = graph.MeasureBlastRadius(scope.OwnedScopes ?? []);
            touchedByCard[scope.CardId.Trim()] = [.. radius.TouchedNodeIds];
            if (!radius.IsFullyMeasured || radius.TouchedNodeIds.Count == 0)
            {
                unverifiable.Add(scope.CardId.Trim());
            }
        }

        // A ordem declarada é indiferente ao sentido em que o código acopla: se o plano já põe A antes
        // de B, uma aresta de código entre eles está coberta nos DOIS sentidos. O que interessa é
        // existir ordem, não adivinhar qual metade dela o grafo enxerga.
        var declaredPairs = declaredEdges
            .Select(edge => PairKey(edge.ProviderCardId, edge.ConsumerCardId))
            .ToHashSet(StringComparer.Ordinal);

        var blocking = new List<PlanGraphDivergence>();
        var advisory = new List<PlanGraphDivergence>();
        var cardIds = touchedByCard.Keys.Order(StringComparer.Ordinal).ToArray();

        for (var i = 0; i < cardIds.Length; i++)
        {
            for (var j = i + 1; j < cardIds.Length; j++)
            {
                var left = cardIds[i];
                var right = cardIds[j];

                // Os dois lados caíram no fallback do papel inteiro (nenhuma superfície
                // reconhecida) — nem a colisão de escopo nem o acoplamento direto do grafo
                // dizem algo confiável sobre ESTES dois cards especificamente, porque o mesmo
                // sinal apareceria entre QUALQUER par que tivesse caído no mesmo fallback. Ver o
                // racional completo no XML-doc de <see cref="Validate"/>.
                if (unnarrowed.Contains(left) && unnarrowed.Contains(right))
                {
                    continue;
                }

                var declared = declaredPairs.Contains(PairKey(left, right));

                var shared = touchedByCard[left]
                    .Intersect(touchedByCard[right], StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (shared.Length > 0 && !declared)
                {
                    blocking.Add(new PlanGraphDivergence(
                        CodeSharedScope,
                        left,
                        right,
                        shared,
                        $"Os cards '{left}' e '{right}' tocam o mesmo código e o plano não " +
                        "estabelece ordem entre eles: despachados juntos, editariam os mesmos " +
                        "tipos ao mesmo tempo."));
                    continue;
                }

                if (declared)
                {
                    continue;
                }

                var coupling = CouplingBetween(graph, touchedByCard[left], touchedByCard[right]);
                if (coupling.Length > 0)
                {
                    blocking.Add(new PlanGraphDivergence(
                        CodeUndeclaredDependency,
                        left,
                        right,
                        coupling,
                        $"O código dos cards '{left}' e '{right}' se referencia diretamente, mas o " +
                        "plano os trata como independentes — a ordem entre eles existe no código e " +
                        "não foi declarada."));
                }
            }
        }

        foreach (var edge in declaredEdges
                     .OrderBy(edge => edge.ProviderCardId, StringComparer.Ordinal)
                     .ThenBy(edge => edge.ConsumerCardId, StringComparer.Ordinal))
        {
            if (!touchedByCard.TryGetValue(edge.ProviderCardId, out var provider) ||
                !touchedByCard.TryGetValue(edge.ConsumerCardId, out var consumer))
            {
                continue;
            }

            // Só é possível dizer "sem evidência" sobre par que o índice conferiu dos dois lados.
            if (unverifiable.Contains(edge.ProviderCardId) ||
                unverifiable.Contains(edge.ConsumerCardId))
            {
                continue;
            }

            var sharesScope = provider.Intersect(consumer, StringComparer.Ordinal).Any();
            if (CouplingBetween(graph, provider, consumer).Length == 0 && !sharesScope)
            {
                advisory.Add(new PlanGraphDivergence(
                    CodeDeclaredWithoutEvidence,
                    edge.ConsumerCardId,
                    edge.ProviderCardId,
                    [],
                    $"O plano declara que '{edge.ConsumerCardId}' depende de " +
                    $"'{edge.ProviderCardId}', e o grafo não mostra acoplamento entre os escopos. " +
                    "Pode ser ordem de negócio, que o código não expressa — fica registrado, não " +
                    "bloqueia."));
            }
        }

        var validated = cardIds
            .Where(cardId => !unverifiable.Contains(cardId))
            .ToArray();

        return new PlanGraphValidation(
            blocking.Count == 0,
            blocking.Count == 0 ? ReasonConsistent : ReasonBlocked,
            [.. blocking
                .OrderBy(divergence => divergence.Code, StringComparer.Ordinal)
                .ThenBy(divergence => divergence.CardId, StringComparer.Ordinal)
                .ThenBy(divergence => divergence.RelatedCardId, StringComparer.Ordinal)],
            [.. advisory],
            validated,
            [.. unverifiable]);
    }

    /// <summary>
    /// Arestas DIRETAS de código entre os dois escopos, nos dois sentidos.
    ///
    /// Direta, e não transitiva, de propósito: num sistema real quase tudo alcança quase tudo por
    /// transitividade (basta passar por um tipo central), e um critério que acusa toda dupla de cards
    /// como dependente não informa nada — só ensina a ignorar o aviso.
    /// </summary>
    private static string[] CouplingBetween(CodeGraph graph, string[] left, string[] right)
    {
        var rightSet = right.ToHashSet(StringComparer.Ordinal);
        var leftSet = left.ToHashSet(StringComparer.Ordinal);

        return [.. graph.Edges
            .Where(edge => edge.Kind != CodeGraphEdgeKind.Contains)
            .Where(edge =>
                (leftSet.Contains(edge.FromNodeId) && rightSet.Contains(edge.ToNodeId)) ||
                (rightSet.Contains(edge.FromNodeId) && leftSet.Contains(edge.ToNodeId)))
            .Select(edge => $"{edge.FromNodeId} -> {edge.ToNodeId}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    private static string PairKey(string first, string second)
    {
        var left = first.Trim();
        var right = second.Trim();
        return string.CompareOrdinal(left, right) <= 0
            ? left + "\u001f" + right
            : right + "\u001f" + left;
    }
}
