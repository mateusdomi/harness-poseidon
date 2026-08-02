using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Harness.Modules.Documents.Application;

/// <summary>
/// Fase 2A.1 — verifica que um documento cumpre a ESTRUTURA do template do playbook.
///
/// Os campos obrigatórios existiam no catálogo desde a migration 0082 e nunca governaram nada:
/// eram lidos por um endpoint de listagem e mais ninguém. Um agente podia produzir uma GMUD sem
/// janela e sem plano de rollback, e o documento entrava no acervo como se estivesse pronto — o
/// campo obrigatório era obrigatório apenas no papel.
///
/// O que esta verificação exige (o FIXO do contrato Fixed/Flexible):
///   * cada campo obrigatório aparece como um cabeçalho de seção ou rótulo forte isolado no corpo;
///   * os cabeçalhos aparecem na ORDEM declarada pelo template.
///
/// O que ela NÃO exige (o FLEXÍVEL): prosa, vocabulário, tom, extensão. O conteúdo de cada seção é
/// do autor — a estrutura é do playbook. Uma verificação que julgasse a prosa transformaria o
/// template numa camisa de força e produziria documentos que passam no gate sem dizer nada.
///
/// A comparação de nomes é tolerante ao que muda sem mudar o sentido — acento, caixa, e o par
/// <c>_</c>/espaço — porque o campo é declarado como `plano_rollback_testado` e o humano escreve
/// "Plano de rollback testado". Recusar por causa disso seria rigor onde não há risco.
/// </summary>
public static class DocumentTemplateCompliance
{
    /// <summary>Resultado da verificação: os campos ausentes e os fora de ordem, nomeados.</summary>
    public sealed record Result(
        IReadOnlyList<string> MissingFields,
        IReadOnlyList<string> OutOfOrderFields)
    {
        public bool IsCompliant => MissingFields.Count == 0 && OutOfOrderFields.Count == 0;

        /// <summary>Mensagem acionável: diz o que falta e o que está fora de ordem, sem jargão.</summary>
        public string Describe()
        {
            var parts = new List<string>(2);
            if (MissingFields.Count > 0)
            {
                parts.Add($"seções obrigatórias ausentes: {string.Join(", ", MissingFields)}");
            }

            if (OutOfOrderFields.Count > 0)
            {
                parts.Add($"seções fora da ordem do template: {string.Join(", ", OutOfOrderFields)}");
            }

            return string.Join("; ", parts);
        }
    }

    /// <summary>
    /// Confere <paramref name="body"/> contra os campos de <paramref name="requiredFieldsJson"/>.
    ///
    /// JSON inválido ou lista vazia devolve conforme: um catálogo malformado é problema do
    /// catálogo e não pode bloquear quem está escrevendo um documento legítimo.
    /// </summary>
    public static Result Check(string? body, string? requiredFieldsJson)
    {
        var required = ParseFields(requiredFieldsJson);
        if (required.Count == 0)
        {
            return new Result([], []);
        }

        var headings = StructuralLabels(body ?? string.Empty)
            .Select(Normalize)
            .ToArray();

        var missing = new List<string>();
        var positions = new List<(string Field, int Position)>();
        foreach (var field in required)
        {
            var normalizedField = Normalize(field);
            var index = Array.FindIndex(
                headings,
                heading => string.Equals(heading, normalizedField, StringComparison.Ordinal) ||
                           heading.StartsWith(normalizedField + " ", StringComparison.Ordinal));
            if (index < 0)
            {
                missing.Add(field);
                continue;
            }

            positions.Add((field, index));
        }

        // Fora de ordem = a seção aparece ANTES de outra que o template declara antes dela. Só as
        // presentes entram na conta: acusar ordem de uma seção ausente seria repetir o mesmo
        // defeito com outro nome.
        var outOfOrder = new List<string>();
        for (var index = 1; index < positions.Count; index++)
        {
            if (positions[index].Position < positions[index - 1].Position)
            {
                outOfOrder.Add(positions[index].Field);
            }
        }

        return new Result(missing, outOfOrder);
    }

    /// <summary>Campos declarados pelo template, na ordem. JSON inválido devolve lista vazia.</summary>
    public static IReadOnlyList<string> ParseFields(string? requiredFieldsJson)
    {
        if (string.IsNullOrWhiteSpace(requiredFieldsJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(requiredFieldsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return document.RootElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Delimitadores estruturais Markdown, na ordem em que aparecem: cabeçalhos ATX
    /// (`#`..`######`) e rótulos fortes isolados (`**contexto**`). O segundo formato é comum em
    /// MADR quando vários registros vivem no mesmo arquivo: o título identifica o ADR e os
    /// rótulos identificam seus campos sem criar uma árvore de sete níveis.
    ///
    /// Texto forte dentro de uma frase e conteúdo de blocos de código não contam. Assim uma
    /// menção em prosa ou um exemplo copiado não consegue satisfazer o gate por acidente.
    /// </summary>
    private static List<string> StructuralLabels(string body)
    {
        var values = new List<string>();
        string? codeFence = null;
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (TryReadFence(line, out var fence))
            {
                codeFence = codeFence is null
                    ? fence
                    : string.Equals(codeFence, fence, StringComparison.Ordinal) ? null : codeFence;
                continue;
            }

            if (codeFence is not null || line.Length == 0)
            {
                continue;
            }

            if (TryReadStrongLabel(line, out var strongLabel))
            {
                values.Add(strongLabel);
                continue;
            }

            if (line[0] != '#')
            {
                continue;
            }

            var hashes = 0;
            while (hashes < line.Length && line[hashes] == '#')
            {
                hashes++;
            }

            if (hashes > 6 || hashes == line.Length)
            {
                continue;
            }

            var text = line[hashes..].Trim();
            if (text.Length > 0)
            {
                values.Add(text);
            }
        }

        return values;
    }

    private static bool TryReadFence(string line, out string fence)
    {
        if (line.StartsWith("```", StringComparison.Ordinal))
        {
            fence = "```";
            return true;
        }

        if (line.StartsWith("~~~", StringComparison.Ordinal))
        {
            fence = "~~~";
            return true;
        }

        fence = string.Empty;
        return false;
    }

    private static bool TryReadStrongLabel(string line, out string label)
    {
        var trimmed = line.Trim();
        var delimiter = trimmed.StartsWith("**", StringComparison.Ordinal)
            ? "**"
            : trimmed.StartsWith("__", StringComparison.Ordinal) ? "__" : null;
        if (delimiter is null)
        {
            label = string.Empty;
            return false;
        }

        var closing = trimmed.IndexOf(delimiter, delimiter.Length, StringComparison.Ordinal);
        if (closing <= delimiter.Length)
        {
            label = string.Empty;
            return false;
        }

        var remainder = trimmed[(closing + delimiter.Length)..].Trim();
        if (remainder.Length > 0 && !string.Equals(remainder, ":", StringComparison.Ordinal))
        {
            label = string.Empty;
            return false;
        }

        label = trimmed[delimiter.Length..closing].Trim().TrimEnd(':').Trim();
        return label.Length > 0;
    }

    /// <summary>
    /// Forma comparável de um nome de seção: sem acento, sem caixa, com `_` e pontuação
    /// reduzidos a espaço único. `plano_rollback_testado` e "Plano de rollback testado" são a
    /// mesma seção — separá-las seria rigor sem risco.
    /// </summary>
    private static string Normalize(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var lastWasSpace = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasSpace = false;
                continue;
            }

            if (!lastWasSpace && builder.Length > 0)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        // Palavras de ligação não distinguem seção: "plano de rollback" e "plano rollback" são a
        // mesma coisa para quem lê, e o template escreve sem elas.
        var words = builder.ToString().Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            // Numeração de outline é apresentação, não identidade do campo: `## 7. decisao_bbir`
            // continua sendo a seção `decisao_bbir`. Todos os tokens numéricos iniciais são
            // descartados para também cobrir cabeçalhos como `### 7.1 Justificativa`.
            .SkipWhile(word => word.All(char.IsDigit))
            .Where(word => !Connectors.Contains(word))
            .ToArray();
        return string.Join(' ', words).Normalize(NormalizationForm.FormC);
    }

    private static readonly HashSet<string> Connectors = new(StringComparer.Ordinal)
    {
        "de", "da", "do", "das", "dos", "e", "o", "a", "os", "as", "em", "no", "na", "para", "por",
    };
}
