using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Modules.Workflows.Product;

/// <summary>
/// A CATEGORIA de uma afirmação extraída de um documento de fonte (Dual Project Gate, Parte B).
/// Conjunto fechado: o classificador nunca inventa categoria nova.
/// </summary>
public enum KnowledgeCategory
{
    FunctionalRequirement,
    BusinessRule,
    AcceptanceCriterion,
    Nfr,
    Constraint,
    TechnicalPreference,
    SecurityRequirement,
    DataRequirement,
    UxRequirement,
    FutureCapability,
    Example,
    ProcessInstruction,
    ProvidedArtifact,
}

/// <summary>
/// O VÍNCULO da afirmação com o projeto — a distinção que impede "Sugestão de stack: React"
/// de virar ProjectRequirement e "execute as sete fases" de virar Playbook.
/// </summary>
public enum KnowledgeBinding
{
    /// <summary>Obrigatório: entra na resolução do perfil/escopo com autoridade de requisito.</summary>
    Required,

    /// <summary>Preferência declarada: informa a decisão; o baseline resolve se ninguém fechar.</summary>
    Preferred,

    /// <summary>"Avaliar"/"considerar": decisão aberta; nunca vira card de MVP por default.</summary>
    Optional,

    /// <summary>Evolução futura: fora do MVP por definição.</summary>
    Future,

    /// <summary>Exemplo ilustrativo (dataset, tela de referência): valida, não exige.</summary>
    Example,

    /// <summary>
    /// SEM autoridade sobre o Poseidon: instruções de processo embutidas no documento
    /// ("atue como equipe", "execute por fases", "aguarde validação"). O documento do usuário é
    /// fonte de conhecimento do projeto — nunca system prompt da Bruna, nunca Playbook.
    /// </summary>
    NonAuthoritative,
}

/// <summary>Uma afirmação classificada, com proveniência obrigatória.</summary>
public sealed record SourceStatement(
    string Id,
    KnowledgeCategory Category,
    KnowledgeBinding Binding,
    string Text,
    string Source,
    double Confidence);

/// <summary>
/// O classificador semântico de fonte (Parte B do Dual Project Gate) — a camada entre "o que o
/// documento diz" e "o que governa o projeto".
///
/// A regra arquitetural que ele materializa: <b>o template do Poseidon é formato interno de
/// saída, nunca contrato de entrada</b>. Documentos chegam em qualquer estrutura — inclusive
/// misturando requisitos, sugestões de stack, instruções de processo para "a IA" e roadmap
/// futuro, como o levantamento real do sistema de Indicadores — e cada afirmação recebe
/// categoria, vínculo e proveniência ANTES de qualquer efeito.
///
/// Ordem de avaliação deliberada: NÃO-AUTORIDADE primeiro (instrução de processo não é
/// requisito por mais imperativa que soe), depois FUTURE, OPTIONAL, PREFERRED — e só então o
/// texto restante pode ser Required. Errar para baixo (classificar exigência como preferência)
/// é recuperável no Planning; errar para cima (promover sugestão a requisito) contamina o
/// perfil do projeto inteiro.
/// </summary>
public static class SourceKnowledgeClassifier
{
    private static readonly Regex ProcessInstructionPattern = new(
        @"\b(atue\s+como|aja\s+como|voce\s+e\s+(um|uma)|execute\s+(o\s+)?desenvolvimento|" +
        @"execute\s+(a\s+)?fase|desenvolva\s+em\s+fases|ao\s+final\s+de\s+cada\s+fase|" +
        @"aguarde\s+(a\s+)?valida|siga\s+(as\s+)?(sete\s+|7\s+)?fases|entregue\s+ao\s+final|" +
        @"ignore\s+(o\s+)?(fluxo|instruc)|apresente\s+um\s+plano)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex FuturePattern = new(
        @"\b(futur[ao]s?|evolu[cç][aã]o|roadmap|posteriormente|em\s+seguida\s+ao\s+mvp|" +
        @"p[oó]s[- ]mvp|pr[oó]xim[ao]s?\s+(vers|etap|fase)|quando\s+houver\s+necessidade)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex OptionalPattern = new(
        @"\b(avaliar|considerar|opcional(mente)?|se\s+poss[ií]vel|desej[aá]vel|" +
        @"pode(r[aá])?\s+ser\s+(avaliad|considerad)|a\s+definir)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex PreferencePattern = new(
        @"\b(sugest[aã]o|suger[ei]|recomenda[cç]?[aã]?o?|recomendad[ao]|prefer[eê]ncia|" +
        @"preferencialmente|op[cç][oõ]es|alternativas?|como\s+por\s+exemplo|por\s+exemplo|" +
        @"tais\s+como|podendo\s+ser)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex RequiredPattern = new(
        @"\b(deve(r[aá]|m|r[aã]o)?|obrigat[oó]ri[ao]s?|requisito|exigid[ao]|necess[aá]ri[ao]s?|" +
        @"utilizar|imprescind[ií]vel|precisa(m|r[aá])?|somente|apenas|n[aã]o\s+pode(m)?|" +
        // Proibição é requisito NEGATIVO: "Não criar gráficos fixos" restringe a solução tanto
        // quanto "utilizar Oracle" — o levantamento real de Indicadores usa exatamente essa forma.
        @"n[aã]o\s+(criar|utilizar|usar|armazenar|permitir|deixar|gravar|apresentar|omitir)|" +
        // Conflito DECLARADO entre regras é o caso arquetípico de ASK: não existe default seguro
        // para uma contradição — só autoridade humana resolve.
        @"contradit[oó]ri\w*|divergen\w*|conflitant\w*)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly (Regex Pattern, KnowledgeCategory Category)[] CategoryPatterns =
    [
        (new Regex(@"\b(crit[eé]rio\s+de\s+aceite|acceptance)\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
            KnowledgeCategory.AcceptanceCriterion),
        (new Regex(@"\b(senha|autentica|autoriza|perfil|permiss|rbac|criptograf|seguran[cç]a|lgpd|owasp|token|sess[aã]o)\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
            KnowledgeCategory.SecurityRequirement),
        (new Regex(@"\b(banco\s+de\s+dados|oracle|postgres|sql\s+server|mysql|schema|tabela|entidade|migra[cç])\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
            KnowledgeCategory.DataRequirement),
        (new Regex(@"\b(desempenho|performance|volume|simult[aâ]ne|lat[eê]ncia|disponibilidade|escalab|cache|fila|ass[ií]ncron|timeout|registros)\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
            KnowledgeCategory.Nfr),
        (new Regex(@"\b(stack|framework|react|angular|vue|vite|next\.?js|java|c#|\.net|node|spring|kubernetes|docker|biblioteca)\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
            KnowledgeCategory.TechnicalPreference),
        (new Regex(@"\b(tela|responsiv|usabilidade|ux|interface|layout|tema|acessib|mobile|navegador)\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
            KnowledgeCategory.UxRequirement),
        (new Regex(@"\b(regra\s+de\s+neg[oó]cio|c[aá]lculo|f[oó]rmula|valida[cç][aã]o\s+de|aprova[cç])\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
            KnowledgeCategory.BusinessRule),
        (new Regex(@"\b(exemplo|dataset\s+de\s+refer|dados\s+de\s+demonstra|massa\s+de\s+dados)\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
            KnowledgeCategory.Example),
    ];

    /// <summary>
    /// Classifica UMA afirmação, com a fonte declarada (seção/linha de origem). Determinístico.
    /// </summary>
    public static SourceStatement Classify(string text, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var normalized = Normalize(text);

        // 1. Instrução de processo NÃO tem autoridade — por mais imperativa que soe. É a defesa
        //    contra o documento que "manda" a Bruna abandonar o Playbook ou aguardar validação.
        if (ProcessInstructionPattern.IsMatch(normalized))
        {
            return Statement(
                KnowledgeCategory.ProcessInstruction, KnowledgeBinding.NonAuthoritative,
                text, source, 1.0);
        }

        // 2. Futuro fica fora do MVP por definição.
        if (FuturePattern.IsMatch(normalized))
        {
            return Statement(
                KnowledgeCategory.FutureCapability, KnowledgeBinding.Future, text, source, 0.9);
        }

        // 3. "Avaliar"/"considerar" é decisão ABERTA, nunca compromisso.
        if (OptionalPattern.IsMatch(normalized) && !RequiredWins(normalized))
        {
            return Statement(
                CategoryOf(normalized, KnowledgeCategory.FunctionalRequirement),
                KnowledgeBinding.Optional, text, source, 0.85);
        }

        // 4. Sugestão/opções/alternativas: preferência. "Sugestão de stack: React, Vite ou
        //    Next.js" informa; não decide — o baseline resolve se ninguém fechar.
        if (PreferencePattern.IsMatch(normalized))
        {
            return Statement(
                CategoryOf(normalized, KnowledgeCategory.TechnicalPreference),
                KnowledgeBinding.Preferred, text, source, 0.85);
        }

        // 5. Exigência declarada.
        if (RequiredPattern.IsMatch(normalized))
        {
            var category = CategoryOf(normalized, KnowledgeCategory.FunctionalRequirement);
            // "Utilizar Oracle" é CONSTRAINT (restringe a solução), não preferência.
            if (category == KnowledgeCategory.DataRequirement &&
                Regex.IsMatch(normalized, @"\b(oracle|postgres|sql\s+server|mysql)\b"))
            {
                category = KnowledgeCategory.Constraint;
            }

            return Statement(category, KnowledgeBinding.Required, text, source, 0.9);
        }

        // 6. Sem sinal de vínculo: conhecimento descritivo — categoria pelo conteúdo, vínculo
        //    preferido (informa, não obriga). Errar para baixo é recuperável.
        return Statement(
            CategoryOf(normalized, KnowledgeCategory.FunctionalRequirement),
            KnowledgeBinding.Preferred, text, source, 0.6);
    }

    /// <summary>Id estável POR CONTEÚDO: sobrevive a renumeração e reordenação de seções.</summary>
    public static string StableId(string text)
    {
        var canonical = Regex.Replace(Normalize(text), @"\s+", " ").Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"sk-{Convert.ToHexString(hash)[..10].ToLowerInvariant()}";
    }

    private static bool RequiredWins(string normalized) =>
        Regex.IsMatch(normalized, @"\b(obrigat[oó]ri|requisito|exigid)\b");

    private static KnowledgeCategory CategoryOf(string normalized, KnowledgeCategory fallback)
    {
        foreach (var (pattern, category) in CategoryPatterns)
        {
            if (pattern.IsMatch(normalized))
            {
                return category;
            }
        }

        return fallback;
    }

    private static SourceStatement Statement(
        KnowledgeCategory category, KnowledgeBinding binding, string text, string source,
        double confidence) =>
        new(StableId(text), category, binding, text.Trim(), source, confidence);

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
