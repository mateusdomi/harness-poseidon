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
///   * cada campo obrigatório aparece como um cabeçalho de seção no corpo;
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

        var headings = Headings(body ?? string.Empty)
            .Select(Normalize)
            .ToArray();

        var missing = new List<string>();
        var positions = new List<(string Field, int Position)>();
        foreach (var field in required)
        {
            var index = Array.IndexOf(headings, Normalize(field));
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

    /// <summary>Cabeçalhos Markdown (`#`..`######`) do corpo, na ordem em que aparecem.</summary>
    private static List<string> Headings(string body)
    {
        var values = new List<string>();
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (line.Length == 0 || line[0] != '#')
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
            .Where(word => !Connectors.Contains(word))
            .ToArray();
        return string.Join(' ', words).Normalize(NormalizationForm.FormC);
    }

    private static readonly HashSet<string> Connectors = new(StringComparer.Ordinal)
    {
        "de", "da", "do", "das", "dos", "e", "o", "a", "os", "as", "em", "no", "na", "para", "por",
    };
}
