namespace Harness.Modules.Workflows.Product;

/// <summary>O que fazer com uma dúvida antes de transformá-la em pergunta ao usuário.</summary>
public enum DecisionRoute
{
    /// <summary>Uma fonte com autoridade já respondeu. Reabrir é reentrevista.</summary>
    Closed,

    /// <summary>Técnica, reversível, com default seguro. Inferir e registrar a premissa.</summary>
    Infer,

    /// <summary>Autoridade do usuário (negócio, escopo, compliance) ou irreversível sem default seguro. Perguntar.</summary>
    Ask,

    /// <summary>Não bloqueia nada agora. Registrar e seguir.</summary>
    Defer,
}

/// <summary>Uma dúvida classificada, com o porquê e o que ela bloqueia.</summary>
public sealed record DecisionRouting(
    DecisionRoute Route,
    string Reason,

    /// <summary>
    /// O que fica parado enquanto a dúvida vive. VAZIO em tudo que não é ASK — e mesmo em ASK, só
    /// a cadeia realmente dependente: uma pergunta humana nunca congela o projeto inteiro.
    /// </summary>
    IReadOnlyList<string> BlockedChain);

/// <summary>
/// A política que decide se uma dúvida vira pergunta — formalizada porque o run real mostrou os
/// dois modos de errar.
///
/// No run de empréstimos, o usuário precisou reenviar a MESMA decisão nove vezes, e um projeto
/// inteiro ficou parado esperando resposta sobre um único documento de observabilidade. Os dois
/// defeitos são espelhados: perguntar o que já foi respondido, e parar o que não depende da
/// resposta.
///
/// As quatro rotas, com quem as aplica em runtime:
///
/// - <b>CLOSED</b> — a precedência do perfil efetivo já resolve (Regulatory &gt; ApprovedDecision
///   &gt; ProjectRequirement &gt; OrganizationConstraint &gt; Baseline), e a ordem de trabalho da
///   entrada rica proíbe reabrir por preferência;
/// - <b>INFER</b> — o resolvedor aplica o baseline e registra o override/premissa; é o caminho de
///   toda escolha técnica reversível com default seguro;
/// - <b>ASK</b> — a escalação existente leva a pergunta ao dono, e o quadro bloqueia SÓ o card
///   escalado: o laço de despacho continua com o resto do backlog, e a cobertura de requisito
///   marca `Blocked` apenas na cadeia dependente;
/// - <b>DEFER</b> — vira registro (premissa/backlog) sem parar nada.
/// </summary>
public static class DecisionPolicy
{
    /// <summary>Os fatos sobre a dúvida. Quem os preenche é quem conhece o contexto.</summary>
    public sealed record DecisionFacts(
        bool AnsweredByAuthoritativeSource,
        bool IsBusinessOrScopeOrCompliance,
        bool IsReversible,
        bool HasSafeDefault,
        bool BlocksDependentWork,
        IReadOnlyList<string>? DependentChain = null);

    public static DecisionRouting Route(DecisionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.AnsweredByAuthoritativeSource)
        {
            return new DecisionRouting(
                DecisionRoute.Closed,
                "Uma fonte com autoridade já respondeu; reabrir por preferência é reentrevista.",
                []);
        }

        // Autoridade do usuário: negócio, escopo e compliance não têm default técnico. O mesmo
        // vale para o irreversível sem rede de proteção — errar aqui não se desfaz com um commit.
        if (facts.IsBusinessOrScopeOrCompliance || (!facts.IsReversible && !facts.HasSafeDefault))
        {
            return facts.BlocksDependentWork
                ? new DecisionRouting(
                    DecisionRoute.Ask,
                    "Autoridade do usuário sem default seguro; bloqueia SÓ a cadeia dependente.",
                    facts.DependentChain ?? [])
                : new DecisionRouting(
                    DecisionRoute.Ask,
                    "Autoridade do usuário, mas nada depende da resposta agora: pergunte sem parar nada.",
                    []);
        }

        if (facts.HasSafeDefault)
        {
            return new DecisionRouting(
                DecisionRoute.Infer,
                "Técnica e com default seguro: aplicar o baseline e registrar a premissa.",
                []);
        }

        return facts.BlocksDependentWork
            ? new DecisionRouting(
                DecisionRoute.Ask,
                "Sem default seguro e com trabalho dependente parado: perguntar é mais barato que errar.",
                facts.DependentChain ?? [])
            : new DecisionRouting(
                DecisionRoute.Defer,
                "Sem resposta, sem default e sem nada parado: registrar e seguir.",
                []);
    }
}
