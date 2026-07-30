using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Modules.Projects.Application;

/// <summary>
/// Criticidade estimada e tecnologias inferidas a partir do que o dono escreveu (D12).
///
/// A decisão homologada tira do dono leigo a tarefa de estimar criticidade — ele descreve o
/// que precisa, não classifica risco. Só que tirar a tarefa de alguém não a faz desaparecer:
/// sem este passo o projeto nascia com o default <c>medium</c> para todo mundo, inclusive para
/// um sistema de pagamento, e esse número seguia para o raio de impacto e a profundidade de
/// revisão como se fosse uma avaliação.
///
/// Duas regras de honestidade moldam o que está aqui:
///
/// 1. <b>Tecnologia não se adivinha, se extrai.</b> Só entra o que o próprio texto nomeia. Um
///    catálogo fechado evita transformar "quero um site de bolos" em "React + Postgres" — o
///    dono não disse isso, e um plano montado sobre pilha inventada é pior que um plano sem
///    pilha nenhuma.
/// 2. <b>Na dúvida, o meio.</b> Sinal de risco eleva; ausência de sinal mantém <c>medium</c>.
///    Só uma demanda declaradamente simples desce para <c>low</c>. A estimativa nunca é o
///    veredito final: a Bruna revisa no primeiro plano e o modo Técnico permite editar.
///
/// Toda estimativa carrega o motivo (<see cref="ProjectIntakeEstimate.Rationale"/>) em
/// linguagem de negócio, porque um número sem explicação é indistinguível de chute.
/// </summary>
public static class ProjectIntakeEstimator
{
    public const string ReasonSensitiveData = "project.criticality.sensitive_data";
    public const string ReasonMoney = "project.criticality.money";
    public const string ReasonAvailability = "project.criticality.availability";
    public const string ReasonSimpleScope = "project.criticality.simple_scope";
    public const string ReasonNoSignal = "project.criticality.no_signal";

    /// <summary>Sinais que elevam o risco, com o motivo que será mostrado ao dono.</summary>
    private static readonly (string Reason, string Explanation, string[] Terms)[] RiskSignals =
    [
        (ReasonMoney,
            "envolve dinheiro (cobrança, pagamento ou nota fiscal)",
            ["pagamento", "pagamentos", "cartao", "cartão", "credito", "crédito", "cobranca",
             "cobrança", "boleto", "pix", "financeiro", "faturamento", "nota fiscal", "checkout",
             "assinatura recorrente", "banco", "bancario", "bancário", "transferencia", "transferência"]),
        (ReasonSensitiveData,
            "guarda dados pessoais sensíveis",
            ["dados bancarios", "dados bancários", "dados pessoais", "cpf", "cnpj", "lgpd",
             "prontuario", "prontuário", "paciente", "saude", "saúde", "medico", "médico",
             "senha", "senhas", "login", "autenticacao", "autenticação", "seguranca", "segurança",
             "confidencial", "sigiloso", "juridico", "jurídico", "contrato"]),
        (ReasonAvailability,
            "precisa ficar no ar sem interrupção",
            ["missao critica", "missão crítica", "critico", "crítico", "alta disponibilidade",
             "24 horas", "24x7", "tempo real", "producao", "produção", "auditado", "auditoria",
             "compliance", "regulatorio", "regulatório"]),
    ];

    /// <summary>Demanda declaradamente pequena e sem consequência — o único caminho para <c>low</c>.</summary>
    private static readonly string[] SimpleScopeTerms =
    [
        "landing page", "site institucional", "portfolio", "portfólio", "blog", "catalogo",
        "catálogo", "cardapio", "cardápio", "pagina simples", "página simples", "one page",
        "protótipo", "prototipo", "teste", "experimento", "rascunho",
    ];

    /// <summary>
    /// Catálogo fechado de tecnologias reconhecíveis: chave de busca → nome canônico.
    /// Fechado de propósito — ver a regra 1 no resumo do tipo.
    /// </summary>
    private static readonly (string Term, string Canonical)[] TechnologyCatalog =
    [
        ("react", "React"), ("next.js", "Next.js"), ("nextjs", "Next.js"), ("angular", "Angular"),
        ("vue", "Vue"), ("svelte", "Svelte"), ("typescript", "TypeScript"), ("javascript", "JavaScript"),
        ("node.js", "Node.js"), ("nodejs", "Node.js"), ("python", "Python"), ("django", "Django"),
        ("flask", "Flask"), ("fastapi", "FastAPI"), ("java", "Java"), ("spring", "Spring"),
        ("kotlin", "Kotlin"), ("swift", "Swift"), ("flutter", "Flutter"), ("react native", "React Native"),
        (".net", ".NET"), ("dotnet", ".NET"), ("c#", "C#"), ("php", "PHP"), ("laravel", "Laravel"),
        ("ruby", "Ruby"), ("rails", "Rails"), ("go", "Go"), ("rust", "Rust"),
        ("postgres", "PostgreSQL"), ("postgresql", "PostgreSQL"), ("mysql", "MySQL"),
        ("sqlite", "SQLite"), ("mongodb", "MongoDB"), ("redis", "Redis"), ("sql server", "SQL Server"),
        ("docker", "Docker"), ("kubernetes", "Kubernetes"), ("aws", "AWS"), ("azure", "Azure"),
        ("google cloud", "Google Cloud"), ("firebase", "Firebase"), ("supabase", "Supabase"),
        ("wordpress", "WordPress"), ("shopify", "Shopify"), ("tailwind", "Tailwind CSS"),
        ("whatsapp", "WhatsApp"), ("telegram", "Telegram"),
    ];

    /// <summary>
    /// Estima a criticidade do projeto a partir do título e do objetivo escritos pelo dono.
    /// </summary>
    public static ProjectIntakeEstimate EstimateCriticality(string? name, string? description)
    {
        var haystack = Normalize($"{name} {description}");
        var matched = RiskSignals
            .Where(signal => signal.Terms.Any(term => ContainsTerm(haystack, term)))
            .ToArray();

        if (matched.Length == 0)
        {
            return SimpleScopeTerms.Any(term => ContainsTerm(haystack, term))
                ? new ProjectIntakeEstimate(
                    "low",
                    ReasonSimpleScope,
                    "Estimei prioridade baixa: o objetivo descreve algo direto, sem dados sensíveis nem dinheiro envolvido.")
                : new ProjectIntakeEstimate(
                    "medium",
                    ReasonNoSignal,
                    "Estimei prioridade média: não encontrei no objetivo nada que indique risco maior. Se houver, me conte que eu ajusto.");
        }

        // Um sinal eleva a alto; dois ou mais somam consequências e vão a crítico.
        var criticality = matched.Length >= 2 ? "critical" : "high";
        var explanation = JoinExplanations(matched.Select(signal => signal.Explanation).ToArray());
        var prefix = criticality == "critical" ? "prioridade máxima" : "prioridade alta";

        return new ProjectIntakeEstimate(
            criticality,
            matched[0].Reason,
            $"Estimei {prefix} porque o objetivo {explanation}. Vou tratar este projeto com revisão mais rigorosa.");
    }

    /// <summary>
    /// Extrai do texto as tecnologias que o próprio dono nomeou. Nunca deduz pilha.
    /// </summary>
    public static IReadOnlyList<string> InferTechnologies(string? name, string? description)
    {
        var haystack = Normalize($"{name} {description}");
        var found = new List<string>();

        foreach (var (term, canonical) in TechnologyCatalog)
        {
            if (ContainsTerm(haystack, term) && !found.Contains(canonical, StringComparer.Ordinal))
            {
                found.Add(canonical);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    /// Casa o termo respeitando fronteira de palavra, para "go" não casar dentro de "google"
    /// e "java" não casar dentro de "javascript". Termos com pontuação (".net", "c#") entram
    /// escapados, e a fronteira à direita aceita fim de palavra ou fim de texto.
    /// </summary>
    private static bool ContainsTerm(string haystack, string term)
    {
        var escaped = Regex.Escape(term);
        var left = char.IsLetterOrDigit(term[0]) ? @"(?<![\p{L}\p{N}])" : @"(?<![\p{L}\p{N}])?";
        var right = char.IsLetterOrDigit(term[^1]) ? @"(?![\p{L}\p{N}])" : string.Empty;

        return Regex.IsMatch(haystack, $"{left}{escaped}{right}", RegexOptions.IgnoreCase);
    }

    /// <summary>Minúsculas sem acento: o dono escreve "crédito" ou "credito" e vale igual.</summary>
    private static string Normalize(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
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

    private static string JoinExplanations(string[] explanations) => explanations.Length switch
    {
        1 => explanations[0],
        2 => $"{explanations[0]} e {explanations[1]}",
        _ => $"{string.Join(", ", explanations[..^1])} e {explanations[^1]}",
    };
}

/// <summary>
/// Criticidade estimada, o código do sinal que a produziu e a explicação em linguagem de
/// negócio. O motivo acompanha o valor porque a tela precisa dizer ao dono POR QUE o projeto
/// dele nasceu com aquela prioridade (D12).
/// </summary>
public sealed record ProjectIntakeEstimate(string Criticality, string ReasonCode, string Rationale);
