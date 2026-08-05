using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;

namespace Harness.Modules.Workflows.Product;

/// <summary>
/// Infere que TIPO de coisa foi pedido a partir da prosa do usuário.
///
/// Esta é a decisão que antecede a stack, e é onde o incidente dos empréstimos começou: "quero um
/// sistema de empréstimos" descreve um resultado que uma pessoa opera, não um endpoint. A
/// inferência é deliberadamente conservadora — uma exclusão explícita ("somente API") vence
/// qualquer palavra que sugira produto, e o desconhecido nunca vira "não precisa de nada".
/// </summary>
public static class ProductModalityInference
{
    /// <summary>
    /// Termos que designam algo operado por uma PESSOA. Quando aparecem sem exclusão explícita, a
    /// modalidade default é web — com interface.
    /// </summary>
    private static readonly string[] HumanOperatedTerms =
    [
        "sistema", "aplicacao", "aplicativo web", "portal", "gestao", "gerenciamento",
        "cadastro", "controle", "painel", "dashboard", "plataforma", "site", "web app",
        "webapp", "crud", "tela", "telas", "interface",
    ];

    private static readonly string[] ApiOnlyTerms =
    [
        "api only", "api-only", "backend only", "backend-only",
        "machine-to-machine", "maquina a maquina", "integracao entre sistemas",
        "sem interface", "sem frontend", "sem tela",
    ];

    /// <summary>
    /// A exclusão explícita raramente vem colada: "somente uma API", "apenas o backend",
    /// "só a API REST". Casar por frase fixa deixaria passar a maioria das formas naturais, e
    /// deixar passar aqui significa exigir uma interface que o usuário disse não querer.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ApiOnlyPhrase = new(
        @"\b(somente|apenas|s[oó])\b(?:\W+\w+){0,3}?\W+\b(api|backend)\b",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly string[] WorkerTerms =
    [
        "worker", "servico windows", "windows service", "daemon", "rotina", "job",
        "agendador", "scheduler", "automacao", "robo", "etl", "processamento em lote",
        "batch",
    ];

    private static readonly string[] DesktopTerms =
    [
        "desktop", "exe", "executavel", "winforms", "wpf", "aplicacao instalavel",
    ];

    private static readonly string[] MobileTerms =
    [
        "mobile", "android", "ios", "aplicativo de celular", "app de celular",
        "aplicativo mobile", "react native", "flutter", "maui",
    ];

    private static readonly string[] LibraryTerms =
    [
        "biblioteca", "library", "pacote nuget", "nuget", "sdk", "componente reutilizavel",
    ];

    public static ProductModality Infer(string? demandText)
    {
        if (string.IsNullOrWhiteSpace(demandText))
        {
            return ProductModality.Unspecified;
        }

        var text = Normalize(demandText);

        // A EXCLUSÃO vem primeiro e vence: "quero um sistema de empréstimos, somente a API" é um
        // pedido de API, mesmo carregando a palavra "sistema".
        if (ContainsAny(text, ApiOnlyTerms) || ApiOnlyPhrase.IsMatch(text))
        {
            return ProductModality.ApiOnly;
        }

        // Declaração EXPLÍCITA de produto web vence menção incidental de componente: o
        // levantamento real de Indicadores diz "sistema web responsivo" e também "biblioteca
        // de gráficos" — e virava Library. O produto é o que o documento DECLARA construir,
        // não a primeira palavra de categoria que aparece numa lista de dependências.
        if (Regex.IsMatch(
                text,
                @"\b(sistema|aplicacao|aplicativo|portal|plataforma)\s+web\b|\bweb\s+responsiv",
                RegexOptions.CultureInvariant))
        {
            return ProductModality.Web;
        }

        if (ContainsAny(text, LibraryTerms))
        {
            return ProductModality.Library;
        }

        if (ContainsAny(text, MobileTerms))
        {
            return ProductModality.Mobile;
        }

        if (ContainsAny(text, DesktopTerms))
        {
            return ProductModality.Desktop;
        }

        if (ContainsAny(text, WorkerTerms))
        {
            return ProductModality.Worker;
        }

        if (ContainsAny(text, HumanOperatedTerms))
        {
            return ProductModality.Web;
        }

        // Silêncio não é "API". Deixar `Unspecified` obriga a Triagem ou a Descoberta a resolver
        // com o usuário, em vez de o executor decidir sozinho no meio da implementação.
        return ProductModality.Unspecified;
    }

    /// <summary>
    /// Termo casa como PALAVRA, nunca como substring. O gate do Prisma pegou o custo do
    /// contrário em 2026-08-05: "ios" dentro de "critérios" classificava como Mobile qualquer
    /// texto em português com "critérios/relatórios/usuários" — e o perfil efetivo do projeto
    /// nascia errado antes do primeiro card.
    /// </summary>
    private static bool ContainsAny(string text, IReadOnlyList<string> terms) =>
        terms.Any(term => System.Text.RegularExpressions.Regex.IsMatch(
            text,
            $@"(?<![\p{{L}}\p{{N}}]){System.Text.RegularExpressions.Regex.Escape(term)}(?![\p{{L}}\p{{N}}])"));

    private static string Normalize(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
