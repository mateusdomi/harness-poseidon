using System.Globalization;
using System.Text;

namespace Harness.Modules.Governance.Context;

/// <summary>
/// Traduz os valores que o runtime conhece para os valores que o manifesto declara.
///
/// O defeito que esta classe existe para fechar: o orquestrador montava o pedido de contexto com
/// <c>workflow="agent-run"</c>, <c>phase="execution"</c> e <c>taskType=&lt;papel da conta&gt;</c>,
/// enquanto o manifesto declarava <c>playbook-standard</c>, fases do playbook e tipos de card. Os
/// dois vocabulários não se cruzavam: medido sobre o manifesto de 41 documentos, um card de código
/// com <c>paths=['src/**']</c> selecionava 11 — e <c>rule-testing</c>, <c>rule-code-review</c>,
/// <c>rule-git</c>, <c>contract-card</c> e <c>contract-handoff</c> ficavam de fora. Documento
/// canônico com o seletor certo segundo o playbook simplesmente nunca chegava ao agente.
///
/// A tradução é bidirecional por construção: cada dimensão devolve o conjunto de valores aceitáveis
/// (o valor cru E a forma canônica), de modo que um manifesto escrito em qualquer um dos dois
/// vocabulários continue casando. Isso mantém as 50 entradas existentes válidas sem reescrita.
/// </summary>
public static class ContextSelectorVocabulary
{
    /// <summary>Fase declarada quando o trabalho não pertence a nenhuma fase do playbook.</summary>
    public const string UnknownPhase = "unspecified";

    /// <summary>Tipo de card declarado quando o produtor não resolveu um tipo.</summary>
    public const string UnknownTaskType = "unspecified";

    /// <summary>
    /// Fases do playbook padrão, do nome materializado no banco para o slug canônico do manifesto.
    /// A chave é o nome literal semeado por <c>CanonicalWorkflowTemplates.PlaybookStandard</c>.
    /// </summary>
    private static readonly Dictionary<string, string> PlaybookPhaseSlugs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["1-triagem"] = "triage",
            ["2-descoberta"] = "discovery",
            ["3-arquitetura"] = "architecture",
            ["4-planejamento"] = "planning",
            ["5-desenvolvimento"] = "development",
            ["6-testes"] = "testing",
            ["7-homologacao"] = "homologation",
            ["8-release"] = "release",
            ["9-sustentacao"] = "sustentation",
            ["arquivada"] = "archived",
            ["roteada-para-sustentacao"] = "sustentation",

            // As mesmas fases sem o prefixo de ordem: a esteira pode renumerar, e um manifesto
            // que declara "desenvolvimento" não deve deixar de casar por causa do "5-".
            ["triagem"] = "triage",
            ["descoberta"] = "discovery",
            ["arquitetura"] = "architecture",
            ["planejamento"] = "planning",
            ["desenvolvimento"] = "development",
            ["testes"] = "testing",
            ["homologacao"] = "homologation",
            ["release"] = "release",
            ["sustentacao"] = "sustentation",
        };

    /// <summary>
    /// Valores aceitáveis para casar a dimensão <c>phases</c> do manifesto.
    /// Devolve o valor cru, a forma normalizada e o slug canônico quando a fase é do playbook.
    /// </summary>
    public static IReadOnlyList<string> PhaseAliases(string? phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
        {
            return [UnknownPhase];
        }

        var raw = phase.Trim();
        var normalized = Normalize(raw);
        var aliases = new List<string>(4) { raw };
        AddDistinct(aliases, normalized);

        if (PlaybookPhaseSlugs.TryGetValue(normalized, out var slug))
        {
            AddDistinct(aliases, slug);
        }

        // "5-desenvolvimento" → "desenvolvimento": um manifesto que declare a fase sem a ordem
        // continua casando quando a esteira renumerar as fases.
        var withoutOrder = StripOrderPrefix(normalized);
        if (!string.Equals(withoutOrder, normalized, StringComparison.Ordinal))
        {
            AddDistinct(aliases, withoutOrder);
            if (PlaybookPhaseSlugs.TryGetValue(withoutOrder, out var bareSlug))
            {
                AddDistinct(aliases, bareSlug);
            }
        }

        return aliases;
    }

    /// <summary>Valores aceitáveis para casar a dimensão <c>taskTypes</c> do manifesto.</summary>
    public static IReadOnlyList<string> TaskTypeAliases(string? cardType)
    {
        if (string.IsNullOrWhiteSpace(cardType))
        {
            return [UnknownTaskType];
        }

        var raw = cardType.Trim();
        var aliases = new List<string>(3) { raw };
        AddDistinct(aliases, Normalize(raw));

        // `agent_task` é o default histórico da tabela `work_tasks`; no vocabulário do playbook o
        // equivalente é `tarefa`. Aceitar os dois evita reescrever cards já persistidos.
        if (string.Equals(Normalize(raw), "agent_task", StringComparison.Ordinal))
        {
            AddDistinct(aliases, "tarefa");
        }

        return aliases;
    }

    /// <summary>Valores aceitáveis para casar a dimensão <c>workflows</c> do manifesto.</summary>
    public static IReadOnlyList<string> WorkflowAliases(string? workflow)
    {
        if (string.IsNullOrWhiteSpace(workflow))
        {
            return ["unspecified"];
        }

        var raw = workflow.Trim();
        var aliases = new List<string>(2) { raw };
        AddDistinct(aliases, Normalize(raw));
        return aliases;
    }

    /// <summary>
    /// Valores aceitáveis para casar a dimensão <c>agents</c>. O manifesto sempre quis dizer
    /// "qual agente/persona", mas o pedido carregava o ALIAS DA CONTA — um identificador de quem
    /// paga a execução, não de quem a executa. Aceitar os dois torna a dimensão utilizável sem
    /// invalidar entrada nenhuma.
    /// </summary>
    public static IReadOnlyList<string> AgentAliases(string? accountAlias, string? personaKey)
    {
        var aliases = new List<string>(4);
        AddDistinct(aliases, accountAlias?.Trim());
        AddDistinct(aliases, personaKey?.Trim());
        AddDistinct(aliases, Normalize(personaKey));
        return aliases.Count == 0 ? ["unspecified"] : aliases;
    }

    /// <summary>Valores aceitáveis para casar a dimensão <c>roles</c> do manifesto.</summary>
    public static IReadOnlyList<string> RoleAliases(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return ["unspecified"];
        }

        var raw = role.Trim();
        var aliases = new List<string>(2) { raw };
        AddDistinct(aliases, Normalize(raw));
        return aliases;
    }

    /// <summary>Minúsculas, sem acento e sem espaço nas bordas — comparação estável entre idiomas.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string StripOrderPrefix(string normalized)
    {
        var separator = normalized.IndexOf('-');
        if (separator <= 0 || separator == normalized.Length - 1)
        {
            return normalized;
        }

        return normalized[..separator].All(char.IsDigit)
            ? normalized[(separator + 1)..]
            : normalized;
    }

    private static void AddDistinct(List<string> aliases, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate) &&
            !aliases.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            aliases.Add(candidate);
        }
    }
}
