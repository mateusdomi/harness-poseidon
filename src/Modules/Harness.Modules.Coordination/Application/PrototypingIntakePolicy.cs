namespace Harness.Modules.Coordination.Application;

/// <summary>Como o material de frontend chegou ao intake.</summary>
public enum PrototypingEntryPath
{
    /// <summary>Prosa: o dono descreveu em palavras. Falta tudo, e a Bruna pergunta só o que falta.</summary>
    Prose = 0,

    /// <summary>Documento de requisitos que já traz especificação de telas.</summary>
    RequirementsDocument = 1,

    /// <summary>ZIP React (tipicamente do Lovable): já é design system pronto.</summary>
    ReactBundle = 2
}

/// <summary>O que a organização já tem, e que a demanda pode HERDAR em vez de refazer.</summary>
public sealed record OrganizationDesignAssets(
    bool HasBrand,
    bool HasDesignSystem,
    bool HasScreenTemplates);

/// <summary>Um anexo do intake, do ponto de vista da detecção.</summary>
public sealed record IntakeAttachment(string FileName, string MediaType, long SizeBytes);

/// <summary>
/// Leitura do intake: caminho detectado, o que foi herdado e o que ainda precisa ser perguntado.
/// <see cref="Questions"/> vazio significa que a Bruna não precisa perguntar nada.
/// </summary>
public sealed record PrototypingIntakeReading(
    PrototypingEntryPath Path,
    IReadOnlyList<string> Inherited,
    IReadOnlyList<string> Questions,
    string ReasonCode);

/// <summary>
/// Inteligência de intake da Prototipação (D11).
///
/// O erro que ela evita é o formulário: perguntar tudo, sempre, a quem já respondeu. Uma organização
/// que tem marca cadastrada e design system não deveria ser interrogada sobre cor primária a cada
/// projeto novo — e um dono que anexou um ZIP React pronto não deveria ser perguntado sobre nada de
/// visual, porque a resposta veio no anexo.
///
/// Herdar não é adivinhar: o que a organização registrou é fato, e usá-lo é continuidade. O que a
/// Bruna NÃO tem, ela pergunta — uma vez, e só o que falta. É a diferença entre um formulário e uma
/// conversa.
///
/// A honestidade fica no registro: a leitura declara explicitamente o que foi herdado e o que foi
/// perguntado, para o dono nunca descobrir depois que a marca do projeto veio de outro lugar sem
/// ninguém dizer.
/// </summary>
public static class PrototypingIntakePolicy
{
    public const string InheritedBrand = "marca da organização";
    public const string InheritedDesignSystem = "design system da organização";
    public const string InheritedTemplates = "modelos de tela da organização";
    public const string InheritedFromBundle = "design system extraído do pacote anexado";

    public const string QuestionBrand = "Qual é a identidade visual? (logo e cores)";
    public const string QuestionScreens = "Quais telas o projeto precisa ter?";

    public const string ReasonBundleProvided = "prototyping.bundle_provided";
    public const string ReasonSpecificationProvided = "prototyping.specification_provided";
    public const string ReasonInheritedFromOrganization = "prototyping.inherited_from_organization";
    public const string ReasonNeedsAnswers = "prototyping.needs_answers";

    private static readonly string[] BundleExtensions = [".zip"];

    private static readonly string[] SpecificationExtensions =
        [".md", ".pdf", ".docx", ".doc", ".txt"];

    /// <summary>
    /// Detecta o caminho de entrada e decide o que herdar e o que perguntar.
    /// <paramref name="mentionsScreens"/> é o fato objetivo de a prosa já descrever telas.
    /// </summary>
    public static PrototypingIntakeReading Read(
        IReadOnlyList<IntakeAttachment> attachments,
        OrganizationDesignAssets assets,
        bool mentionsScreens = false)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(assets);

        var inherited = new List<string>();
        if (assets.HasBrand)
        {
            inherited.Add(InheritedBrand);
        }

        if (assets.HasDesignSystem)
        {
            inherited.Add(InheritedDesignSystem);
        }

        if (assets.HasScreenTemplates)
        {
            inherited.Add(InheritedTemplates);
        }

        // ZIP React responde a pergunta visual inteira: perguntar depois dele seria ignorar o que o
        // dono acabou de entregar.
        if (attachments.Any(IsBundle))
        {
            return new PrototypingIntakeReading(
                PrototypingEntryPath.ReactBundle,
                [.. inherited, InheritedFromBundle],
                [],
                ReasonBundleProvided);
        }

        if (attachments.Any(IsSpecification))
        {
            var questions = assets.HasBrand ? new List<string>() : [QuestionBrand];
            return new PrototypingIntakeReading(
                PrototypingEntryPath.RequirementsDocument,
                inherited,
                questions,
                questions.Count == 0 ? ReasonSpecificationProvided : ReasonNeedsAnswers);
        }

        var proseQuestions = new List<string>();
        if (!assets.HasBrand)
        {
            proseQuestions.Add(QuestionBrand);
        }

        if (!assets.HasScreenTemplates && !mentionsScreens)
        {
            proseQuestions.Add(QuestionScreens);
        }

        return new PrototypingIntakeReading(
            PrototypingEntryPath.Prose,
            inherited,
            proseQuestions,
            proseQuestions.Count == 0 ? ReasonInheritedFromOrganization : ReasonNeedsAnswers);
    }

    private static bool IsBundle(IntakeAttachment attachment) =>
        BundleExtensions.Any(extension =>
            attachment.FileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool IsSpecification(IntakeAttachment attachment) =>
        SpecificationExtensions.Any(extension =>
            attachment.FileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Motivos de recusa de um pacote anexado.</summary>
public enum BundleRejection
{
    None = 0,
    NotAnArchive = 1,
    Empty = 2,
    TooLarge = 3,
    MissingFrontendEntry = 4
}

public sealed record BundleValidation(BundleRejection Rejection, string BusinessMessage)
{
    public bool IsValid => Rejection == BundleRejection.None;
}

/// <summary>
/// Validação do pacote React anexado.
///
/// A recusa aqui é sempre em linguagem de negócio, e isso não é enfeite: quem anexa o ZIP é o dono,
/// não um desenvolvedor. "MissingFrontendEntry" não diz nada a ele; "não encontrei as telas dentro do
/// arquivo" diz o que fazer.
/// </summary>
public static class ReactBundleValidator
{
    /// <summary>Limite generoso: pacote de telas do Lovable raramente passa disto.</summary>
    public const long MaxSizeBytes = 200L * 1024 * 1024;

    public static BundleValidation Validate(
        IntakeAttachment attachment,
        IReadOnlyList<string> entryNames)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(entryNames);

        if (!attachment.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return new BundleValidation(
                BundleRejection.NotAnArchive,
                "Esse arquivo não é um pacote compactado. Envie o .zip que você baixou das telas.");
        }

        if (attachment.SizeBytes <= 0 || entryNames.Count == 0)
        {
            return new BundleValidation(
                BundleRejection.Empty,
                "O pacote chegou vazio. Pode reenviar?");
        }

        if (attachment.SizeBytes > MaxSizeBytes)
        {
            return new BundleValidation(
                BundleRejection.TooLarge,
                "Esse pacote é grande demais para eu processar. Se der, envie só a pasta das telas.");
        }

        var hasFrontend = entryNames.Any(entry =>
            entry.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
            entry.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase) ||
            entry.EndsWith("package.json", StringComparison.OrdinalIgnoreCase));
        if (!hasFrontend)
        {
            return new BundleValidation(
                BundleRejection.MissingFrontendEntry,
                "Não encontrei as telas dentro do arquivo. Confira se o pacote é o do projeto de telas.");
        }

        return new BundleValidation(BundleRejection.None, "Pacote recebido e validado.");
    }
}
