using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Governance.Coordination;

namespace Harness.Host.Agents;

/// <summary>
/// Resolve QUAL profissional (persona) e QUAL papel de escopo uma demanda (card) exige — a
/// heurística INICIAL de planejamento do chefe. É deliberadamente simples e melhorável (a
/// fase de excelência refina isto): mapeia palavras da demanda para a persona especializada do
/// catálogo e para o papel de escopo (frontend/backend). Um card que já declare a persona
/// prevalece sobre a heurística.
/// </summary>
public static class ChiefCardResolver
{
    // Personas semeadas no catálogo (agent_key). A demanda vai ao profissional certo.
    public const string Engineer = "software-engineer";
    public const string Architect = "software-architect";
    public const string TechnicalWriter = "technical-writer";
    public const string ProductAnalyst = "product-requirements-analyst";
    public const string CriticQa = "critic-qa";

    public static ChiefCardResolution Resolve(
        string title, string instructionBody, IReadOnlyList<string> acceptanceCriteria,
        string riskTier, string? explicitPersonaKey = null, string? explicitRole = null,
        RepositorySurfaceMap? surfaceMap = null,
        IReadOnlyList<string>? personaAllowedScopes = null,
        IReadOnlyList<string>? personaDeniedScopes = null,
        string? cardType = null)
    {
        // CARD-OBJETIVO (perfil v2, Understand → Build → Prove): um executor persistente dono do
        // repositório inteiro por um objetivo funcional. Nada de heurística nem estreitamento de
        // escopo — os claims largos do papel são o mecanismo de serialização por projeto, e a
        // persona é o engenheiro salvo declaração explícita.
        if (string.Equals(cardType, "objetivo", StringComparison.Ordinal))
        {
            var objectiveRole = AgentRoles.ProjectExecutor;
            var objectiveClaims = AgentRoles.PathScopesFor(objectiveRole);
            var objectiveCard = new DelegationCard(
                title, instructionBody, acceptanceCriteria, objectiveClaims, riskTier);
            var objectivePersona = explicitPersonaKey
                ?? ParseDeclaredSpecialty(instructionBody)
                ?? Engineer;
            return new ChiefCardResolution(
                objectiveRole, "code", objectivePersona, objectiveClaims, objectiveCard,
                Engineer,
                new CardPathScopePlan(objectiveClaims, false, "objective.full_repository", []));
        }

        // A heurística lê a DEMANDA, não os marcadores de despacho. A linha "Especialidade
        // exigida: architecture-security" carrega a palavra "architecture" — deixá-la no palheiro
        // faria a persona declarada enviesar o próprio fallback dela, e uma chave inválida
        // arrastaria a inferência junto.
        var text = $"{title}\n{WithoutSpecialtyMarker(instructionBody)}".ToLowerInvariant();

        // Um card que DECLARA a capacidade operacional (o materializer escreve
        // "Capacidade de execução autorizada: <role>") prevalece sobre qualquer heurística. O
        // marcador legado "Papel exigido" continua aceito para instruções imutáveis já gravadas.
        var role = explicitRole ?? ParseDeclaredRole(instructionBody) ?? InferRole(text);

        // A persona INFERIDA é sempre calculada, mesmo quando há uma declarada: ela é o fallback
        // usado quando a chave declarada não existe (ou não está habilitada) no catálogo. Sem esse
        // fallback, uma chave inválida escrita pelo modelo deixaria o card sem persona alguma e o
        // briefing degradaria para o texto cru do escopo — o executor perderia o "quem/como".
        var inferred = InferPersona(text, role);
        var persona = explicitPersonaKey ?? ParseDeclaredSpecialty(instructionBody) ?? inferred;

        // ESCOPO POR CARD, não por papel. Enquanto todo card de backend reivindicava `src/**`,
        // dois cards independentes do mesmo projeto nunca rodavam juntos — o paralelismo escalava
        // por papéis, e não pelo trabalho. O planejador estreita quando reconhece a superfície
        // real no repositório e devolve o escopo do papel quando não reconhece: claim estreito
        // demais trava o agente no meio, o que é pior do que um claim amplo que só serializa.
        //
        // F-17: os escopos declarados pela persona são vinculantes DENTRO do teto do papel.
        // O papel impõe o máximo que o card pode reivindicar; a persona reduz esse máximo
        // para as áreas onde aquele profissional de fato trabalha. Sem isso, duas fontes de
        // escopo coexistiam e só a do papel era aplicada — a persona declarava escopos que
        // ninguém lia.
        var roleClaims = AgentRoles.PathScopesFor(role);
        var baseClaims = ApplyPersonaScopeHints(roleClaims, personaAllowedScopes, personaDeniedScopes);
        var plan = CardPathScopePlanner.Plan(
            baseClaims, surfaceMap ?? RepositorySurfaceMap.Empty, title, instructionBody);
        var claims = plan.Claims;
        var capability = string.Equals(role, AgentRoles.Critic, StringComparison.OrdinalIgnoreCase)
            ? "review"
            : "code";

        var card = new DelegationCard(
            title,
            instructionBody,
            acceptanceCriteria,
            claims,
            riskTier);

        return new ChiefCardResolution(role, capability, persona, claims, card, inferred, plan);
    }

    /// <summary>
    /// Especialidade declarada no corpo da instrução ("Especialidade exigida: &lt;agent_key&gt;"),
    /// escrita pelo materializador quando o Chefe declarou quem é o profissional qualificado. Aqui
    /// só o formato é validado; a EXISTÊNCIA da persona é conferida contra o catálogo no despacho,
    /// porque autoridade é do sistema, nunca do texto que o modelo produziu.
    /// </summary>
    private const string SpecialtyMarker = "especialidade exigida:";

    private static string WithoutSpecialtyMarker(string instructionBody) =>
        string.Join(
            '\n',
            instructionBody.Split('\n').Where(line =>
                !line.Trim().StartsWith(SpecialtyMarker, StringComparison.OrdinalIgnoreCase)));

    private static string? ParseDeclaredSpecialty(string instructionBody)
    {
        const string marker = SpecialtyMarker;
        foreach (var line in instructionBody.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var declared = trimmed[marker.Length..].Trim();
            return declared.Length is > 0 and <= 100 ? declared : null;
        }

        return null;
    }

    /// <summary>
    /// Capacidade operacional declarada no corpo da instrução. Ela escolhe a conta e o escopo de
    /// execução; não é o papel humano do profissional, que vem de "Especialidade exigida". O
    /// marcador legado "Papel exigido" permanece compatível com cards já persistidos. "none" e
    /// valores desconhecidos caem para a heurística.
    /// </summary>
    private static string? ParseDeclaredRole(string instructionBody)
    {
        string[] markers = ["capacidade de execução autorizada:", "papel exigido:"];
        foreach (var line in instructionBody.Split('\n'))
        {
            var trimmed = line.Trim();
            var marker = markers.FirstOrDefault(candidate =>
                trimmed.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (marker is null)
            {
                continue;
            }

            var declared = trimmed[marker.Length..].Trim();
            if (string.Equals(declared, AgentRoles.BackendSpecialist, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(declared, AgentRoles.FrontendSpecialist, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(declared, AgentRoles.Critic, StringComparison.OrdinalIgnoreCase))
            {
                return declared.ToLowerInvariant();
            }

            return null;
        }

        return null;
    }

    // "ui"/"ux" como SUBSTRING viravam falso positivo em português ("concluir", "incluir",
    // "possui" contêm "ui") e mandavam card de backend para o papel de frontend. Os termos
    // curtos exigem fronteira de palavra; os longos continuam por substring.
    private static string InferRole(string text)
    {
        // "componente" saiu da lista de frontend porque é vocabulário de ARQUITETURA antes de
        // ser de interface: o próprio C4 tem um nível chamado Componente. Com ele lá dentro,
        // os cards da Fase 3 da prova limpa (SAD, C4, ADRs, DER, threat model, plano de
        // observabilidade) resolviam para `frontend-specialist` — e como só a conta de
        // frontend serve esse papel, a fase inteira parou quando aquela conta caiu, com
        // `role_not_allowed` em todas as outras. A persona já dizia "Arquiteto"; o papel dizia
        // outra coisa.
        //
        // E o enquadramento de ARQUITETURA vem antes: um SAD de quatro mil caracteres cita
        // "tela" uma vez, e uma única ocorrência num texto longo passava a decidir o papel do
        // card inteiro. Trabalho de arquitetura é trabalho de arquitetura mesmo quando fala de
        // interface — a especialização de frontend é para quem vai MEXER na interface, não
        // para quem a descreve. O papel passa a seguir o mesmo enquadramento que já escolhia
        // a persona, em vez de contradizê-lo.
        if (IsArchitectureWork(text))
        {
            return AgentRoles.BackendSpecialist;
        }

        if (MentionsAny(text, "frontend", "front-end", " ui ", "ui/", "/ui", " ux ", " tela", "css", "react"))
        {
            return AgentRoles.FrontendSpecialist;
        }

        return AgentRoles.BackendSpecialist;
    }

    private static bool IsArchitectureWork(string text) =>
        MentionsAny(text, "arquitetura", "architecture", "adr", "fronteira", "boundary", "decisão técnica");

    private static string InferPersona(string text, string role)
    {
        if (IsArchitectureWork(text))
        {
            return Architect;
        }

        if (MentionsAny(text, "document", "documenta", "readme", "guia", "manual", "changelog"))
        {
            return TechnicalWriter;
        }

        if (MentionsAny(text, "requisito", "requirement", "critério de aceite", "escopo do produto", "user story"))
        {
            return ProductAnalyst;
        }

        // Padrão: o engenheiro de software implementa a fatia (frontend ou backend).
        return Engineer;
    }

    private static bool MentionsAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Aplica os escopos declarados pela persona como restrição adicional sobre o escopo do papel.
    /// A persona pode REDUZIR o escopo, nunca ampliar: um claim do papel só permanece se estiver
    /// dentro de pelo menos um <paramref name="allowedScopes"/>, e é removido se estiver dentro de
    /// qualquer <paramref name="deniedScopes"/>. Escopos vazios ou ausentes não restringem nada.
    /// </summary>
    private static IReadOnlyList<string> ApplyPersonaScopeHints(
        IReadOnlyList<string> roleClaims,
        IReadOnlyList<string>? allowedScopes,
        IReadOnlyList<string>? deniedScopes)
    {
        var allowed = allowedScopes ?? [];
        var denied = deniedScopes ?? [];
        if (allowed.Count == 0 && denied.Count == 0)
        {
            return roleClaims;
        }

        var filtered = roleClaims
            .Where(roleClaim =>
            {
                if (allowed.Count > 0 && !allowed.Any(personaScope => IsWithin(roleClaim, personaScope)))
                {
                    return false;
                }

                return !denied.Any(deniedScope => IsWithin(roleClaim, deniedScope));
            })
            .ToArray();

        // Se a persona restringiu TUDO, é sinal de configuração inconsistente: melhor voltar ao
        // escopo do papel e deixar a política de path scope recusar de forma auditável, em vez de
        // produzir um card silenciosamente sem escrito.
        return filtered.Length > 0 ? filtered : roleClaims;
    }

    /// <summary>
    /// O path candidato está contido no escopo raiz? Tolerância para varredura (<c>/**</c>) dos
    /// dois lados: <c>src/Modules/X/**</c> está dentro de <c>src/**</c>.
    /// </summary>
    private static bool IsWithin(string candidate, string root)
    {
        var path = candidate.Replace('\\', '/').Trim('/').TrimEnd('*').TrimEnd('/');
        var rootBase = root.Replace('\\', '/').Trim('/').TrimEnd('*').TrimEnd('/');
        if (rootBase.Length == 0 || path.Length == 0)
        {
            return false;
        }

        return path.Equals(rootBase, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith($"{rootBase}/", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Resolução de um card: o papel de escopo, a capacidade exigida, a persona especializada
/// (agent_key do catálogo), os claims autorizados e a demanda pronta para compor o briefing.
/// </summary>
public sealed record ChiefCardResolution(
    string Role,
    string RequiredCapability,
    string PersonaKey,
    IReadOnlyList<string> ScopeClaims,
    DelegationCard Card,

    /// <summary>
    /// A persona que a heurística escolheria. Igual a <see cref="PersonaKey"/> quando nada foi
    /// declarado; é o fallback quando a chave declarada não existe no catálogo.
    /// </summary>
    string InferredPersonaKey = "",

    /// <summary>
    /// Como o escopo foi decidido: estreitado para as superfícies reconhecidas do repositório ou
    /// herdado do papel, e por quê. É o que permite auditar por que dois cards colidiram.
    /// </summary>
    CardPathScopePlan? ScopePlan = null);
