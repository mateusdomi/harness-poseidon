using System.Globalization;
using System.Text;

namespace Harness.Persistence.Abstractions.Agents;

/// <summary>
/// Gera a chave (slug) de uma definição de agente de forma DETERMINÍSTICA e versionada
/// (P1/auto-key). A chave respeita o mesmo alfabeto validado pelo store — minúsculas,
/// dígitos e hífen, no máximo 100 caracteres — de modo que uma chave gerada nunca é
/// recusada pela validação.
///
/// A geração é pura (sem IO): deriva um slug base do nome e, quando ele colide com uma
/// chave existente, acrescenta um sufixo NUMÉRICO crescente (`-2`, `-3`, …). O sufixo é a
/// "versão" da chave para o mesmo nome, escolhida de forma estável e case-insensitive.
/// </summary>
public static class AgentKeyGenerator
{
    public const int MaxLength = 100;

    /// <summary>Slug usado quando o nome não produz nenhum caractere válido.</summary>
    public const string Fallback = "agent";

    /// <summary>
    /// Deriva o slug base de um nome: remove acentos, baixa a caixa, troca qualquer
    /// caractere fora de <c>[a-z0-9]</c> por hífen, colapsa hifens repetidos, apara as
    /// bordas e limita o comprimento. Nome sem caractere aproveitável vira <see cref="Fallback"/>.
    /// </summary>
    public static string Derive(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fallback;
        }

        var normalized = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var previousHyphen = false;
        foreach (var character in normalized)
        {
            // Descarta marcas de combinação (acentos) mantendo a letra base: `ç`→`c`, `ã`→`a`.
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(character);
            if (char.IsAsciiLetterLower(lower) || char.IsAsciiDigit(lower))
            {
                builder.Append(lower);
                previousHyphen = false;
            }
            else if (!previousHyphen)
            {
                builder.Append('-');
                previousHyphen = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > MaxLength)
        {
            slug = slug[..MaxLength].Trim('-');
        }

        return slug.Length == 0 ? Fallback : slug;
    }

    /// <summary>
    /// Gera uma chave ÚNICA a partir do nome, evitando as chaves já existentes
    /// (case-insensitive). Se o slug base já existe, acrescenta o menor sufixo `-N` (N ≥ 2)
    /// que não colida, truncando a base quando necessário para caber em <see cref="MaxLength"/>.
    /// </summary>
    public static string Generate(string? name, IEnumerable<string>? existingKeys = null)
    {
        var taken = new HashSet<string>(
            (existingKeys ?? []).Where(key => !string.IsNullOrWhiteSpace(key)),
            StringComparer.OrdinalIgnoreCase);

        var baseSlug = Derive(name);
        if (!taken.Contains(baseSlug))
        {
            return baseSlug;
        }

        for (var version = 2; ; version++)
        {
            var suffix = $"-{version.ToString(CultureInfo.InvariantCulture)}";
            var head = baseSlug.Length + suffix.Length > MaxLength
                ? baseSlug[..(MaxLength - suffix.Length)].TrimEnd('-')
                : baseSlug;
            var candidate = head + suffix;
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
