using System.Globalization;

namespace Harness.Modules.Agents.Application.Accounts;

/// <summary>Tipo fechado da solicitação de um agente. Texto livre nunca é discriminador.</summary>
public enum AgentRequestKind
{
    NeedsDecision,
    NeedsClarification,
    BlockedByDependency,
    ScopeExpansion,
    ExternalResource,
    CanonicalConflict,
}

/// <summary>Para onde a solicitação vai.</summary>
public enum AgentRequestRouting
{
    /// <summary>A chefe resolve sozinha: é decisão operacional dela.</summary>
    ChiefResolves,

    /// <summary>Só o humano pode resolver — recurso ou autoridade que o sistema não possui.</summary>
    EscalateToHuman,
}

/// <summary>
/// Decide QUEM responde a uma solicitação de agente.
///
/// A regra de produto: o dono é o stakeholder que delegou o projeto. Escolher padrão técnico,
/// dividir classe, criar migration, trocar de conta, refazer teste ou resolver conflito
/// operacional é trabalho da chefe — levar isso ao dono o transforma em gargalo de uma fábrica que
/// existe para não depender dele. O que sobe é o que o sistema genuinamente não pode resolver:
/// dinheiro, credencial que ele não tem, decisão legal ou regulatória, recurso externo
/// inacessível, conflito real entre as fontes canônicas.
///
/// É PURA para poder ser provada sem Host, e CONSERVADORA na direção certa: um tipo desconhecido
/// fica com a chefe, porque escalar por dúvida treina o dono a ignorar o canal.
/// </summary>
public static class AgentRequestPolicy
{
    public static AgentRequestKind ParseKind(string? value) =>
        (value ?? string.Empty).Trim().ToLower(CultureInfo.InvariantCulture) switch
        {
            "needs_clarification" => AgentRequestKind.NeedsClarification,
            "blocked_by_dependency" => AgentRequestKind.BlockedByDependency,
            "scope_expansion" => AgentRequestKind.ScopeExpansion,
            "external_resource" => AgentRequestKind.ExternalResource,
            "canonical_conflict" => AgentRequestKind.CanonicalConflict,
            _ => AgentRequestKind.NeedsDecision,
        };

    public static string Serialize(AgentRequestKind kind) => kind switch
    {
        AgentRequestKind.NeedsClarification => "needs_clarification",
        AgentRequestKind.BlockedByDependency => "blocked_by_dependency",
        AgentRequestKind.ScopeExpansion => "scope_expansion",
        AgentRequestKind.ExternalResource => "external_resource",
        AgentRequestKind.CanonicalConflict => "canonical_conflict",
        _ => "needs_decision",
    };

    /// <summary>
    /// Sinais de que a solicitação depende do MUNDO EXTERNO, e não de uma escolha técnica. São
    /// deliberadamente concretos: "custo", "assinatura", "cartão", "contrato" — não "importante"
    /// nem "urgente", que qualquer texto pode alegar.
    /// </summary>
    /// A precisão importa mais do que a abrangência: um falso escalonamento devolve o dono à
    /// posição de gargalo, que é exatamente o que este desenho existe para desfazer. Por isso
    /// termos ambíguos no vocabulário do próprio produto ficam de fora — "contrato" quase sempre
    /// significa contrato de API aqui, e "licença" costuma ser licença de software já resolvida.
    private static readonly string[] ExternalSignals =
    [
        "assinatura", "subscription", "pagamento", "payment", "cartão", "cartao", "fatura",
        "comprar", "compra", "contratar", "contrato assinado", "cláusula", "clausula",
        "credencial", "credential", "api key", "apikey", "token de acesso", "acesso externo",
        "conta externa", "provedor externo", "jurídic", "juridic", "regulat",
        "lgpd", "compliance", "auditoria externa", "custo adicional", "orçamento", "orcamento",
    ];

    /// <summary>
    /// Roteia a solicitação. <paramref name="text"/> é a pergunta somada ao motivo — usada só para
    /// os tipos em que a natureza externa não está no próprio tipo.
    /// </summary>
    public static AgentRequestRouting Route(AgentRequestKind kind, string text)
    {
        switch (kind)
        {
            // O tipo já declara a natureza: recurso externo e conflito canônico são exatamente as
            // duas coisas que a chefe não tem como resolver sozinha.
            case AgentRequestKind.ExternalResource:
            case AgentRequestKind.CanonicalConflict:
                return AgentRequestRouting.EscalateToHuman;

            // Expansão de escopo é decisão de governança do trabalho, não do negócio: a policy
            // valida os paths e a chefe decide. Nunca sobe.
            case AgentRequestKind.ScopeExpansion:
                return AgentRequestRouting.ChiefResolves;

            default:
                break;
        }

        // Para dúvida, esclarecimento e dependência, o que decide é o CONTEÚDO: uma dependência
        // bloqueada por falta de credencial é externa; bloqueada por outro card não é.
        var haystack = $" {(text ?? string.Empty).ToLower(CultureInfo.InvariantCulture)} ";
        return ExternalSignals.Any(signal => haystack.Contains(signal, StringComparison.Ordinal))
            ? AgentRequestRouting.EscalateToHuman
            : AgentRequestRouting.ChiefResolves;
    }
}
