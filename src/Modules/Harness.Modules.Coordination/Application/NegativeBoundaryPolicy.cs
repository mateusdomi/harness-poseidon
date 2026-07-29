namespace Harness.Modules.Coordination.Application;

/// <summary>Um card e os escopos que ele legitimamente mexe.</summary>
public sealed record CardScope(string CardId, string Title, IReadOnlyList<string> OwnedScopes);

/// <summary>
/// Fronteira negativa de um card: o que NÃO tocar, e de quem é. O motivo importa tanto quanto a
/// proibição — "não toque em X" convida a contornar; "não toque em X, é da tarefa Y" explica que
/// existe alguém do outro lado.
/// </summary>
public sealed record NegativeBoundary(
    string Scope,
    string OwnerCardId,
    string OwnerTitle)
{
    public string Describe() =>
        $"Não altere {Scope} — é da tarefa \"{OwnerTitle}\" ({OwnerCardId}).";
}

/// <summary>
/// Fronteiras negativas no card (B11).
///
/// Num plano com vários cards em voo, o dano mais comum não é o agente que falha: é o que trabalha
/// bem demais. Ele vê um defeito adjacente, conserta "de brinde", e o resultado é conflito de merge
/// com o card que era dono daquele arquivo — ou pior, duas correções diferentes do mesmo problema,
/// nenhuma das quais o revisor pediu.
///
/// Dizer ao agente apenas o que fazer não impede isso, porque a iniciativa parece virtude. O que
/// impede é dizer o que NÃO é dele e de quem é: a fronteira deixa de parecer burocracia e passa a
/// parecer respeito ao colega. Por isso a frase carrega o card irmão e o título dele.
///
/// A regra é derivada, não escrita à mão: o planner conhece os escopos de todos os cards, então a
/// fronteira de cada um é simplesmente "os escopos dos irmãos menos os meus".
/// </summary>
public static class NegativeBoundaryPolicy
{
    /// <summary>
    /// Calcula as fronteiras de <paramref name="cardId"/> a partir dos escopos dos irmãos.
    /// Escopo que o próprio card também possui NÃO entra: fronteira contra si mesmo travaria o
    /// trabalho, e é assim que uma regra de proteção viraria motivo de escalação inútil.
    /// </summary>
    public static IReadOnlyList<NegativeBoundary> Derive(
        string cardId,
        IReadOnlyList<CardScope> plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        ArgumentNullException.ThrowIfNull(plan);

        var self = plan.FirstOrDefault(card =>
            string.Equals(card.CardId, cardId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Card '{cardId}' não está no plano.", nameof(cardId));

        var own = self.OwnedScopes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return plan
            .Where(card => !string.Equals(card.CardId, cardId, StringComparison.Ordinal))
            .SelectMany(card => card.OwnedScopes
                .Where(scope => !own.Contains(scope))
                .Select(scope => new NegativeBoundary(scope, card.CardId, card.Title)))
            .DistinctBy(boundary => boundary.Scope, StringComparer.OrdinalIgnoreCase)
            .OrderBy(boundary => boundary.Scope, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Trecho injetado no bundle do agente. Vazio quando não há irmãos — e nesse caso não se
    /// inventa aviso, porque instrução sem função ensina o agente a ignorar instruções.
    /// </summary>
    public static string ComposeBundleSection(IReadOnlyList<NegativeBoundary> boundaries)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        if (boundaries.Count == 0)
        {
            return string.Empty;
        }

        var lines = boundaries.Select(boundary => $"- {boundary.Describe()}");
        return
            "## Fronteiras desta tarefa\n\n" +
            "Outras pessoas da equipe estão trabalhando em paralelo. Consertar algo fora do seu\n" +
            "escopo cria conflito com o trabalho delas, mesmo quando o conserto está certo.\n" +
            "Se encontrar um problema fora daqui, relate em vez de corrigir.\n\n" +
            string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// Preenche o <c>OutOfScope</c> de cada card do plano com as fronteiras derivadas dos irmãos.
    ///
    /// É aqui que a regra deixa de ser teoria: o planner já sabe o escopo de todos os cards, então
    /// cada card pode receber, no próprio enunciado, o que não é dele e de quem é. O texto que o
    /// planner escreveu é PRESERVADO — as fronteiras se somam a ele, porque apagar a instrução
    /// original para caber uma regra derivada seria perder informação que só o planner tinha.
    ///
    /// Cards são identificados pelo título, e não por id, porque no momento do plano eles ainda não
    /// têm id — e o título é justamente o que faz sentido para o agente do outro lado.
    /// </summary>
    public static DemandPlanProposal EnrichWithSiblingBoundaries(DemandPlanProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Cards.Count <= 1)
        {
            // Um card sozinho não tem irmão: nada a declarar, e nada a inventar.
            return proposal;
        }

        var scopes = proposal.Cards
            .Select(card => new CardScope(card.ProposedTitle, card.ProposedTitle, [card.InScope]))
            .ToArray();

        var enriched = proposal.Cards
            .Select(card =>
            {
                var boundaries = Derive(card.ProposedTitle, scopes);
                if (boundaries.Count == 0)
                {
                    return card;
                }

                var declared = string.Join(
                    " ",
                    boundaries.Select(boundary =>
                        $"Não altere {boundary.Scope} — é da tarefa \"{boundary.OwnerTitle}\"."));

                return card with
                {
                    OutOfScope = string.IsNullOrWhiteSpace(card.OutOfScope)
                        ? declared
                        : $"{card.OutOfScope.TrimEnd()} {declared}"
                };
            })
            .ToArray();

        return proposal with { Cards = enriched };
    }

    /// <summary>
    /// Verdadeiro quando um caminho tocado viola a fronteira. Comparação por prefixo de escopo,
    /// porque escopo é raiz de trabalho e não caminho exato.
    /// </summary>
    public static NegativeBoundary? FindViolation(
        string touchedPath,
        IReadOnlyList<NegativeBoundary> boundaries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(touchedPath);
        ArgumentNullException.ThrowIfNull(boundaries);

        var normalized = touchedPath.Replace('\\', '/').Trim();
        return boundaries.FirstOrDefault(boundary =>
            normalized.StartsWith(
                boundary.Scope.Replace('\\', '/').TrimEnd('/') + "/",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                normalized,
                boundary.Scope.Replace('\\', '/').TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase));
    }
}
