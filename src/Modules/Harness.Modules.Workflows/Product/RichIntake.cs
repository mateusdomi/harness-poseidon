using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Modules.Workflows.Product;

/// <summary>Como um projeto ENTROU: o que decide quanto de descoberta ainda faz sentido.</summary>
public enum IntakeFlow
{
    /// <summary>
    /// Intenção crua ("quero um sistema para X"). A descoberta extensa é o trabalho certo: as
    /// perguntas ainda não foram respondidas por ninguém.
    /// </summary>
    Discovery,

    /// <summary>
    /// Entrada RICA: requisitos escritos, protótipo fornecido, restrições e prazo. Reentrevistar é
    /// desperdício e reescrever a fonte é pior — o trabalho certo é validar, normalizar, rastrear
    /// e preencher só as lacunas genuínas.
    /// </summary>
    Accelerator,
}

/// <summary>O que se sabe sobre uma informação que o playbook precisa.</summary>
public enum InformationSufficiency
{
    /// <summary>A fonte fornecida já responde. Perguntar de novo é reentrevista.</summary>
    Answered,

    /// <summary>Não respondida, mas um default seguro do baseline/organização cobre. Inferir e registrar.</summary>
    Inferable,

    /// <summary>Sem resposta E sem default seguro, e o trabalho dependente não pode começar sem ela.</summary>
    BlockingUnknown,

    /// <summary>Sem resposta, mas nada para. Registrar e seguir.</summary>
    NonBlockingUnknown,
}

/// <summary>Um sinal estruturado detectado na entrada, com a origem que o prova.</summary>
public sealed record IntakeSignal(string Kind, string Source);

/// <summary>
/// O veredito do intake: qual fluxo este projeto pede, com os sinais que o provam.
/// </summary>
public sealed record IntakeAssessment(
    IntakeFlow Flow,
    IReadOnlyList<IntakeSignal> Signals)
{
    public bool Has(string kind) => Signals.Any(signal => signal.Kind == kind);

    public string Summary() =>
        $"{Flow.ToString().ToLowerInvariant()}:{string.Join(',', Signals.Select(s => s.Kind))}";
}

/// <summary>
/// Distingue, EM RUNTIME, a entrada crua da entrada rica.
///
/// Por que isto não pode ser só canon: o canon `provided-artifacts` já dizia "valide, normalize,
/// rastreie, preencha lacunas — não reentreviste". Mas uma norma que depende de o agente lembrar
/// dela é uma norma que falha exatamente no card mais caro. A classificação aqui é determinística,
/// e é o runtime que injeta a ordem de trabalho correspondente na instrução do card — o agente
/// recebe o modo de trabalho, não a esperança de que o descubra.
///
/// A detecção usa SINAIS ESTRUTURADOS, nunca uma palavra mágica:
/// - anexo com papel `requirements_source` (documento de requisitos fornecido);
/// - anexo com papel `provided_frontend` (protótipo/interface existente);
/// - critérios de aceite numerados no texto;
/// - declaração de fonte única de verdade;
/// - override explícito de stack (banco, linguagem, framework);
/// - prazo do projeto.
///
/// Um sinal isolado não converte o fluxo: prazo sozinho, ou um único "deve ser Oracle", ainda é
/// descoberta. O fluxo vira ACCELERATOR quando existe FONTE DE CONTEÚDO (requisitos ou protótipo)
/// — porque é ela que torna a reentrevista um desperdício.
/// </summary>
public static class RichIntakeAnalyzer
{
    public const string SignalRequirementsSource = "requirements_source";
    public const string SignalProvidedFrontend = "provided_frontend";
    public const string SignalAcceptanceCriteria = "acceptance_criteria";
    public const string SignalSourceOfTruthDeclared = "source_of_truth_declared";
    public const string SignalStackOverride = "stack_override";
    public const string SignalDeadline = "deadline";

    private static readonly Regex AcceptanceCriteriaPattern = new(
        @"crit[ée]rios? de aceite|acceptance criteria|given\s*/?\s*when\s*/?\s*then",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex SourceOfTruthPattern = new(
        @"fonte ([úu]nica )?d[ae] verdade|source of truth|build[- ]ready",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Avalia a entrada. <paramref name="attachmentRolesWithNames"/> são os anexos da solicitação
    /// com o papel tipado da migration 0127 — é a proveniência, não a adivinhação, que decide.
    /// </summary>
    public static IntakeAssessment Analyze(
        string? demandText,
        IReadOnlyList<(string Role, string FileName)> attachmentRolesWithNames,
        bool hasDeadline,
        IReadOnlyList<ProfileDirective>? directives = null)
    {
        ArgumentNullException.ThrowIfNull(attachmentRolesWithNames);
        var signals = new List<IntakeSignal>();

        foreach (var (role, fileName) in attachmentRolesWithNames)
        {
            if (string.Equals(role, "requirements_source", StringComparison.Ordinal))
            {
                signals.Add(new IntakeSignal(SignalRequirementsSource, $"anexo:{fileName}"));
            }
            else if (string.Equals(role, "provided_frontend", StringComparison.Ordinal))
            {
                signals.Add(new IntakeSignal(SignalProvidedFrontend, $"anexo:{fileName}"));
            }
        }

        var text = demandText ?? string.Empty;
        if (AcceptanceCriteriaPattern.IsMatch(text))
        {
            signals.Add(new IntakeSignal(SignalAcceptanceCriteria, "texto da demanda"));
        }

        if (SourceOfTruthPattern.IsMatch(text))
        {
            signals.Add(new IntakeSignal(SignalSourceOfTruthDeclared, "texto da demanda"));
        }

        if (directives is { Count: > 0 })
        {
            foreach (var directive in directives.Where(item =>
                item.Authority >= ProfileAuthority.ProjectRequirement))
            {
                signals.Add(new IntakeSignal(SignalStackOverride, $"{directive.Area}={directive.Value}"));
            }
        }

        if (hasDeadline)
        {
            signals.Add(new IntakeSignal(SignalDeadline, "projeto"));
        }

        // FONTE DE CONTEÚDO decide o fluxo. Prazo e override sozinhos não: um pedido cru com prazo
        // continua precisando de descoberta — o que não se pode fazer é reperguntar o que um
        // documento fornecido já respondeu.
        var hasContentSource = signals.Any(signal =>
            signal.Kind is SignalRequirementsSource or SignalProvidedFrontend or
                SignalSourceOfTruthDeclared);

        return new IntakeAssessment(
            hasContentSource ? IntakeFlow.Accelerator : IntakeFlow.Discovery,
            signals);
    }

    /// <summary>
    /// A ORDEM DE TRABALHO que o runtime injeta na instrução de um card de documento quando o
    /// fluxo é ACCELERATOR. É a diferença entre norma e comportamento: o agente recebe o modo
    /// certo, com as fontes nomeadas, em vez de depender de lembrar do canon.
    /// </summary>
    public static string ComposeWorkOrder(IntakeAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (assessment.Flow != IntakeFlow.Accelerator)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("\n# MODO DE TRABALHO: ENTRADA RICA (validar, não reentrevistar)\n");
        builder.Append(
            "Este projeto NÃO entrou como intenção crua: o usuário forneceu fonte de conteúdo, " +
            "listada abaixo. O fluxo é VALIDAR → NORMALIZAR → RASTREAR → PREENCHER LACUNAS.\n\n");
        foreach (var signal in assessment.Signals)
        {
            builder.Append("- SINAL `").Append(signal.Kind).Append("`: ").Append(signal.Source)
                .Append('\n');
        }

        builder.Append('\n');
        builder.Append(
            "- Informação que a fonte fornecida JÁ RESPONDE: use-a e cite a origem. NÃO pergunte " +
            "de novo, NÃO reescreva o conteúdo dela em outro documento — referencie.\n");
        builder.Append(
            "- Decisão marcada como fechada na fonte NÃO se reabre por preferência técnica. Só " +
            "conflito objetivo (contradição interna, impossibilidade comprovada, choque " +
            "regulatório) reabre, e com registro.\n");
        builder.Append(
            "- Lacuna genuína continua sendo lacuna: o documento responder 80% não dispensa " +
            "tratar os 20%. Classifique cada lacuna como INFERÍVEL (resolva pelo baseline e " +
            "registre a premissa) ou BLOQUEANTE (pergunte só ela; o resto do trabalho segue).\n");
        builder.Append(
            "- Artefato fornecido com papel `provided_frontend` EXISTE para ser evoluído: " +
            "inspecione, preserve, complete e integre. Substituir por design próprio exige " +
            "decisão registrada.\n");
        return builder.ToString();
    }
}
