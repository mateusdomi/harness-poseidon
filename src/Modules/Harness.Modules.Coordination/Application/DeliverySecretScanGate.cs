using System.Text.RegularExpressions;

namespace Harness.Modules.Coordination.Application;

/// <summary>Um segredo encontrado na entrega, com o padrão que o reconheceu e a linha onde está.</summary>
/// <param name="PatternName">Nome do formato de credencial reconhecido (nunca o valor).</param>
/// <param name="File">Arquivo do diff onde a linha adicionada está, quando o cabeçalho permitiu deduzir.</param>
/// <param name="Excerpt">Trecho da linha com o segredo JÁ REDIGIDO — evidência sem vazamento.</param>
public sealed record SecretScanFinding(string PatternName, string File, string Excerpt);

public sealed record SecretScanVerdict(bool IsClean, IReadOnlyList<SecretScanFinding> Findings)
{
    public string Detail => IsClean
        ? "nenhum segredo de alta precisão nas linhas adicionadas"
        : string.Join(
            "; ",
            Findings.Select(finding => $"{finding.PatternName} em {finding.File}: {finding.Excerpt}"));
}

/// <summary>
/// Varredura de segredo sobre o DIFF de uma entrega — a metade genérica da camada determinística
/// (<see cref="VerificationLayer.Deterministic"/>), a que independe do stack e por isso pode rodar
/// desde o primeiro card de código.
///
/// A camada se declarava "build, testes e varredura de segredo" e, na ausência de veredito, era
/// montada como aprovada: ausência de verificação virava aprovação. Isso não doeu enquanto todo
/// card era de DOCUMENTO e o gate documental alimentava a camada; o Desenvolvimento é a primeira
/// fase que entrega código, e o critério do portão dela diz literalmente "sem segredo em código".
///
/// Duas decisões de projeto, ambas para não trocar um risco conhecido por um pior:
///
/// 1. <b>Só linhas ADICIONADAS.</b> Linha removida é o oposto de um vazamento — punir a remoção de
///    um segredo ensinaria a não removê-lo. Cabeçalhos (<c>+++</c>) também não contam.
/// 2. <b>Só formatos concretos de credencial.</b> É o mesmo critério que o repositório já aplica aos
///    próprios arquivos rastreados (<c>tools/backend/scan-secrets.sh</c> e o gate de higiene da
///    suíte de arquitetura): alta precisão para não produzir falso-positivo. O padrão genérico
///    <c>password = ...</c> ficou de fora DE PROPÓSITO — ele casa com quase todo código de
///    autenticação legítimo, e um bloqueio falso aqui devolve o card para correções em laço, que é
///    exatamente o desperdício que a operação está tentando eliminar. Segredo sem formato
///    reconhecível continua sendo trabalho da revisão comportamental.
/// </summary>
public static partial class DeliverySecretScanGate
{
    public const string ReasonClean = "secret_scan.clean";
    public const string ReasonSecretFound = "secret_scan.secret_in_delivery";

    /// <summary>Teto de achados reportados: a evidência precisa caber num parecer legível.</summary>
    private const int MaxFindings = 10;

    private static readonly (string Name, Regex Pattern)[] HighSignalPatterns =
    [
        ("telegram-bot-token", TelegramToken()),
        ("private-key-block", PrivateKeyBlock()),
        ("aws-access-key-id", AwsAccessKey()),
        ("slack-token", SlackToken()),
        ("google-api-key", GoogleApiKey()),
        ("anthropic-api-key", AnthropicKey()),
        ("openai-api-key", OpenAiKey()),
        ("github-token", GitHubToken()),
    ];

    /// <summary>
    /// Varre o diff unificado da tentativa. Diff vazio é limpo: não há linha adicionada.
    /// </summary>
    public static SecretScanVerdict Inspect(string? diff)
    {
        if (string.IsNullOrWhiteSpace(diff))
        {
            return new SecretScanVerdict(true, []);
        }

        var findings = new List<SecretScanFinding>();
        var currentFile = "(arquivo desconhecido)";

        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                currentFile = NormalizeFilePath(line[4..].Trim());
                continue;
            }

            if (line.Length == 0 || line[0] != '+')
            {
                continue;
            }

            var added = line[1..];
            foreach (var (name, pattern) in HighSignalPatterns)
            {
                if (!pattern.IsMatch(added))
                {
                    continue;
                }

                findings.Add(new SecretScanFinding(name, currentFile, Redact(added)));
                if (findings.Count >= MaxFindings)
                {
                    return new SecretScanVerdict(false, findings);
                }

                break;
            }
        }

        return new SecretScanVerdict(findings.Count == 0, findings);
    }

    /// <summary>
    /// Compõe o veredito na camada determinística. Recebe o resultado que os outros gates já
    /// produziram e só o rebaixa — segredo encontrado nunca é compensado por diagnóstico limpo.
    /// </summary>
    public static LayerResult ApplyTo(LayerResult layer, SecretScanVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(verdict);

        return verdict.IsClean
            ? layer
            : new LayerResult(
                layer.Layer, LayerVerdict.Fail, ReasonSecretFound, verdict.Detail);
    }

    /// <summary>
    /// A evidência precisa provar o achado sem republicar o segredo — quem lê o parecer não pode
    /// receber a credencial de volta em texto claro no ledger.
    /// </summary>
    private static string Redact(string line)
    {
        var trimmed = line.Trim();
        var visible = trimmed.Length <= 24 ? trimmed.Length / 3 : 8;
        return string.Concat(trimmed.AsSpan(0, visible), "…[REDIGIDO]");
    }

    private static string NormalizeFilePath(string header)
    {
        if (header.StartsWith("b/", StringComparison.Ordinal))
        {
            header = header[2..];
        }

        var tab = header.IndexOf('\t', StringComparison.Ordinal);
        return tab >= 0 ? header[..tab] : header;
    }

    [GeneratedRegex(@"[0-9]{6,10}:AA[A-Za-z0-9_-]{30,}", RegexOptions.NonBacktracking)]
    private static partial Regex TelegramToken();

    [GeneratedRegex(
        @"-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----",
        RegexOptions.NonBacktracking)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"AKIA[0-9A-Z]{16}", RegexOptions.NonBacktracking)]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"xox[baprs]-[0-9A-Za-z-]{10,}", RegexOptions.NonBacktracking)]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"AIza[0-9A-Za-z_-]{35}", RegexOptions.NonBacktracking)]
    private static partial Regex GoogleApiKey();

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_-]{16,}", RegexOptions.NonBacktracking)]
    private static partial Regex AnthropicKey();

    [GeneratedRegex(@"sk-(?:proj-)?[A-Za-z0-9_-]{20,}", RegexOptions.NonBacktracking)]
    private static partial Regex OpenAiKey();

    [GeneratedRegex(@"gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.NonBacktracking)]
    private static partial Regex GitHubToken();
}
