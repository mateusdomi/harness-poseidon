using System.Text;
using System.Text.Json;

namespace Harness.Modules.Coordination.Application;

/// <summary>Um manifesto encontrado na entrega: onde ele está e o que ele declara saber rodar.</summary>
/// <param name="RelativeDirectory">Diretório do manifesto, relativo à raiz da worktree ("" = raiz).</param>
/// <param name="Content">Conteúdo cru do <c>package.json</c>.</param>
public sealed record DeliveryManifest(string RelativeDirectory, string Content);

/// <summary>
/// COMO a entrega declara o gate. A plataforma reconhece as duas formas que uma entrega honesta
/// usa para dizer "é assim que se roda isto", e nenhuma outra.
/// </summary>
public enum DeliveryGateKind
{
    /// <summary>Script da seção <c>scripts</c> de um <c>package.json</c> entregue.</summary>
    NpmScript = 1,

    /// <summary>Script executável em <c>tools/&lt;área&gt;/&lt;gate&gt;.sh</c>, a convenção deste repositório.</summary>
    ShellScript = 2
}

/// <summary>
/// Um gate que a ENTREGA declara saber executar. Não é texto livre do agente: ou é um nome de
/// script dentro do manifesto, ou é um arquivo no caminho convencionado — nos dois casos, um
/// artefato versionado que o revisor vê no diff.
/// </summary>
/// <param name="Target">Nome do script (npm) ou caminho relativo do arquivo (shell).</param>
public sealed record DeliveryGateCommand(
    string Gate,
    DeliveryGateKind Kind,
    string Target,
    string RelativeDirectory,
    bool RequiresExternalDependencies)
{
    public string Display => Kind switch
    {
        DeliveryGateKind.NpmScript => string.IsNullOrEmpty(RelativeDirectory)
            ? $"npm run {Target}"
            : $"{RelativeDirectory}$ npm run {Target}",
        _ => $"bash {Target}"
    };
}

/// <summary>O que a plataforma vai executar, ou por que não vai.</summary>
public sealed record DeliveryGatePlan(
    IReadOnlyList<DeliveryGateCommand> Commands,
    string ReasonCode,
    string Detail);

/// <summary>Resultado de UM comando de gate: o fato, com a saída truncada como evidência.</summary>
public sealed record DeliveryGateOutcome(
    string Gate,
    string Display,
    bool Passed,
    int ExitCode,
    bool TimedOut,
    string Output);

/// <summary>
/// O que a camada determinística de fato apurou sobre os gates desta entrega.
/// <see cref="ReasonCode"/> é sempre um dos códigos de <see cref="DeliveryGateExecutionPolicy"/>.
/// </summary>
public sealed record DeliveryGateReport(
    IReadOnlyList<DeliveryGateOutcome> Outcomes,
    string ReasonCode,
    string Detail)
{
    /// <summary>Verdadeiro só quando TODO gate exigido rodou de fato e passou.</summary>
    public bool AllPassed =>
        string.Equals(ReasonCode, DeliveryGateExecutionPolicy.ReasonPassed, StringComparison.Ordinal);

    /// <summary>
    /// Verdadeiro quando nenhum gate era exigido — o card não é de código. Aqui a camada segue
    /// valendo o que os outros gates disseram: não há nada a rebaixar nem a aprovar.
    /// </summary>
    public bool NotApplicable =>
        string.Equals(ReasonCode, DeliveryGateExecutionPolicy.ReasonNotRequired, StringComparison.Ordinal);
}

/// <summary>
/// A terceira metade da camada determinística (<see cref="VerificationLayer.Deterministic"/>): a
/// que EXECUTA os gates que o card exige, em vez de pedir que o ator prove tê-los executado.
///
/// O defeito que isto corrige (OPS-071) não era de nenhum dos dois lados. O pacote de objetivo
/// declara "Gates: build, tests"; o revisor cobra a prova de execução, corretamente; e a CLI que
/// executa o card roda com sandbox própria que NEGA rodar Node dentro da worktree. O ator então
/// entrega dizendo por escrito que não pôde executar, e o revisor reprova por isso — com achado P0,
/// duas vezes, por dois revisores independentes. Enquanto valesse, nenhum card de código podia ser
/// aprovado, qualquer que fosse a qualidade da entrega.
///
/// Quem tem que rodar o gate é a PLATAFORMA. É a função normal de um gate de CI.
///
/// <b>A fronteira, declarada junto com a correção e não depois dela</b> — porque isto executa, no
/// host, código que um agente acabou de escrever:
///
/// 1. <b>O comando vem do manifesto da própria entrega</b>, e só dele. O nome do script é escolhido
///    de uma lista fechada (<c>build</c>, <c>test</c>, <c>lint</c>, <c>typecheck</c>) casada contra
///    a seção <c>scripts</c> do <c>package.json</c> entregue. Nenhum texto livre do agente vira
///    linha de comando: se o script não estiver declarado no manifesto, não há o que rodar.
/// 2. <b>Sem rede.</b> O executor entrega um ambiente mínimo, sem as variáveis do processo do Host
///    (portanto sem nenhum segredo) e com o cliente de pacote em modo offline. Consequência aceita e
///    declarada: um gate que dependa de dependência externa não instalada NÃO roda, e ausência de
///    execução continua bloqueando — nunca aprovando.
/// 3. <b>Teto de tempo</b> por comando e no total, imposto pelo executor.
/// 4. <b>Saída capturada e truncada</b> vira evidência no parecer, com início e fim preservados: a
///    linha que explica costuma estar num dos dois extremos, e esta operação já perdeu uma causa
///    para um truncamento que só guardou a cauda.
/// </summary>
public static class DeliveryGateExecutionPolicy
{
    /// <summary>O card não exige gate executável — não há o que apurar aqui.</summary>
    public const string ReasonNotRequired = "delivery_gates.not_required";

    /// <summary>Todo gate exigido rodou e passou.</summary>
    public const string ReasonPassed = "delivery_gates.passed";

    /// <summary>Um gate exigido rodou e falhou. Fato objetivo: reprovação determinística.</summary>
    public const string ReasonFailed = "delivery_gates.failed";

    /// <summary>A entrega exige gate e não trouxe manifesto que declare como rodá-lo.</summary>
    public const string ReasonManifestMissing = "delivery_gates.manifest_missing";

    /// <summary>O manifesto existe e não declara o script do gate exigido.</summary>
    public const string ReasonScriptMissing = "delivery_gates.script_missing";

    /// <summary>O gate depende de dependência externa que não pode ser instalada sem rede.</summary>
    public const string ReasonDependenciesUnavailable = "delivery_gates.dependencies_unavailable";

    /// <summary>Não há runtime no host para executar o gate declarado.</summary>
    public const string ReasonRuntimeUnavailable = "delivery_gates.runtime_unavailable";

    /// <summary>Teto de comandos por gate — um repositório com servidor e interface declara dois.</summary>
    private const int MaxCommandsPerGate = 3;

    /// <summary>Gates que a plataforma sabe executar, e os nomes de script que os satisfazem.</summary>
    private static readonly (string Gate, string[] Scripts)[] KnownGates =
    [
        ("build", ["build"]),
        ("test", ["test", "tests"]),
        ("lint", ["lint"]),
        ("typecheck", ["typecheck", "type-check"]),
    ];

    /// <summary>Sinônimos aceitos na linha "Gates:" do pacote de objetivo.</summary>
    private static readonly Dictionary<string, string> GateAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["build"] = "build",
            ["compilacao"] = "build",
            ["compilação"] = "build",
            ["test"] = "test",
            ["tests"] = "test",
            ["teste"] = "test",
            ["testes"] = "test",
            ["lint"] = "lint",
            ["typecheck"] = "typecheck",
            ["type-check"] = "typecheck",
        };

    /// <summary>
    /// Lê a linha <c>Gates: ...</c> do pacote versionado da delegação. É a MESMA linha que o revisor
    /// cobra — usar outra fonte reintroduziria a divergência entre o que se exige e o que se apura.
    /// Nome de gate que a plataforma não sabe executar é ignorado aqui e continua sendo trabalho da
    /// revisão comportamental; inventar uma execução para ele seria pior que não ter.
    /// </summary>
    public static IReadOnlyList<string> ParseRequiredGates(string? delegationInstruction)
    {
        if (string.IsNullOrWhiteSpace(delegationInstruction))
        {
            return [];
        }

        var gates = new List<string>();
        foreach (var rawLine in delegationInstruction.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("Gates:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var token in line[6..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (GateAliases.TryGetValue(token, out var gate) && !gates.Contains(gate))
                {
                    gates.Add(gate);
                }
            }
        }

        return gates;
    }

    /// <summary>
    /// Traduz um <c>package.json</c> entregue nas declarações de gate que ele contém. Puro: quem
    /// lê disco é o executor.
    /// </summary>
    public static IReadOnlyList<DeliveryGateCommand> DeclarationsFromManifest(
        DeliveryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var parsed = ParseManifest(manifest.Content);
        if (parsed is null)
        {
            return [];
        }

        return
        [
            .. KnownGates.SelectMany(known => known.Scripts
                .Where(script => parsed.Scripts.Contains(script))
                .Take(1)
                .Select(script => new DeliveryGateCommand(
                    known.Gate,
                    DeliveryGateKind.NpmScript,
                    script,
                    manifest.RelativeDirectory,
                    parsed.HasExternalDependencies)))
        ];
    }

    /// <summary>
    /// O gate que um caminho de script convencionado declara, ou <see langword="null"/>.
    /// Aceita <c>tools/&lt;área&gt;/&lt;gate&gt;.sh</c> e <c>tools/&lt;gate&gt;.sh</c> — e nada
    /// mais: o conjunto fechado é o que impede um arquivo qualquer da entrega de virar comando.
    /// </summary>
    public static string? GateForScriptPath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3 ||
            !string.Equals(parts[0], "tools", StringComparison.OrdinalIgnoreCase) ||
            !parts[^1].EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = parts[^1][..^3];
        return GateAliases.TryGetValue(name, out var gate) ? gate : null;
    }

    /// <summary>
    /// Planeja o que rodar. Nunca devolve comando que a ENTREGA não tenha declarado — seja como
    /// script no manifesto, seja como arquivo no caminho convencionado.
    ///
    /// As duas formas existem porque a primeira entrega real desta operação usou a segunda: o ator
    /// de backend escolheu Python sem dependência de terceiro e declarou os gates em
    /// <c>tools/backend/{build,test}.sh</c>, espelhando a convenção do próprio repositório que ele
    /// estava lendo. Reconhecer só <c>package.json</c> teria tornado essa entrega — a única
    /// aprovada até aqui — impossível de verificar, que é exatamente o defeito que este gate
    /// existe para corrigir, só que do outro lado.
    /// </summary>
    public static DeliveryGatePlan Plan(
        IReadOnlyList<string> requiredGates,
        IReadOnlyList<DeliveryGateCommand> declarations)
    {
        ArgumentNullException.ThrowIfNull(requiredGates);
        ArgumentNullException.ThrowIfNull(declarations);

        if (requiredGates.Count == 0)
        {
            return new DeliveryGatePlan([], ReasonNotRequired, "o card não exige gate executável");
        }

        if (declarations.Count == 0)
        {
            return new DeliveryGatePlan(
                [],
                ReasonManifestMissing,
                $"a entrega exige {string.Join(", ", requiredGates)} e não declara como executá-los " +
                "(nem `scripts` num package.json entregue, nem tools/<área>/<gate>.sh)");
        }

        var commands = new List<DeliveryGateCommand>();
        var missing = new List<string>();
        foreach (var gate in requiredGates)
        {
            // TODAS as declarações do gate são executadas, não a primeira: num repositório com
            // servidor e interface, verificar só uma das fatias e chamar isso de "build passou"
            // seria a mesma meia-verdade que o `Pass` fabricado do OPS-064. O teto de tempo total
            // é quem limita.
            var matches = declarations
                .Where(declaration => string.Equals(declaration.Gate, gate, StringComparison.Ordinal))
                .Take(MaxCommandsPerGate)
                .ToList();

            if (matches.Count == 0)
            {
                missing.Add(gate);
                continue;
            }

            commands.AddRange(matches);
        }

        if (missing.Count > 0)
        {
            return new DeliveryGatePlan(
                commands,
                ReasonScriptMissing,
                $"a entrega não declara como executar: {string.Join(", ", missing)}");
        }

        var withDependencies = commands.Where(command => command.RequiresExternalDependencies).ToList();
        if (withDependencies.Count > 0)
        {
            return new DeliveryGatePlan(
                commands,
                ReasonDependenciesUnavailable,
                "o manifesto declara dependências externas e a plataforma executa os gates SEM " +
                $"rede ({string.Join(", ", withDependencies.Select(command => command.Display))}). " +
                "Declare gates que rodem com a biblioteca padrão do runtime.");
        }

        return new DeliveryGatePlan(
            commands,
            ReasonPassed,
            $"{commands.Count} gate(s) planejado(s) a partir do manifesto entregue");
    }

    /// <summary>
    /// Compõe o relatório final a partir do que o executor observou. É aqui que "planejado" vira
    /// "apurado": um plano que não virou execução nunca é reportado como passagem.
    /// </summary>
    public static DeliveryGateReport Consolidate(
        DeliveryGatePlan plan,
        IReadOnlyList<DeliveryGateOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outcomes);

        if (!string.Equals(plan.ReasonCode, ReasonPassed, StringComparison.Ordinal))
        {
            // O plano já sabia que não daria para apurar. O motivo dele é o motivo do relatório.
            return new DeliveryGateReport(outcomes, plan.ReasonCode, plan.Detail);
        }

        if (outcomes.Count < plan.Commands.Count)
        {
            var executed = outcomes.Select(outcome => outcome.Gate).ToHashSet(StringComparer.Ordinal);
            var notRun = plan.Commands
                .Where(command => !executed.Contains(command.Gate))
                .Select(command => command.Display)
                .ToList();
            return new DeliveryGateReport(
                outcomes,
                ReasonRuntimeUnavailable,
                $"gate planejado que não chegou a executar: {string.Join(", ", notRun)}");
        }

        var failed = outcomes.Where(outcome => !outcome.Passed).ToList();
        return failed.Count == 0
            ? new DeliveryGateReport(
                outcomes,
                ReasonPassed,
                string.Join("; ", outcomes.Select(outcome => $"{outcome.Display}: OK")))
            : new DeliveryGateReport(
                outcomes,
                ReasonFailed,
                string.Join(
                    "; ",
                    failed.Select(outcome => outcome.TimedOut
                        ? $"{outcome.Display}: estourou o teto de tempo"
                        : $"{outcome.Display}: saiu com código {outcome.ExitCode}")));
    }

    /// <summary>
    /// Rebaixa a camada determinística conforme o que os gates apuraram. Nunca PROMOVE: um gate
    /// verde não compensa segredo encontrado nem diagnóstico vermelho.
    /// </summary>
    public static LayerResult ApplyTo(LayerResult layer, DeliveryGateReport report)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(report);

        // Já havia uma falha apurada por outro gate (diagnóstico, segredo). Escrever a nossa por
        // cima seria a quarta ocorrência de "o envelope apaga a causa" nesta operação: quem lê o
        // parecer receberia "gate falhou" onde a causa era um segredo na entrega. A primeira
        // reprovação é a mais objetiva e é a que fica.
        if (layer.Verdict == LayerVerdict.Fail)
        {
            return layer;
        }

        if (report.NotApplicable || report.AllPassed)
        {
            return report.AllPassed && layer.Verdict == LayerVerdict.Pass
                ? layer with { ReasonCode = ReasonPassed, Detail = report.Detail }
                : layer;
        }

        // Gate exigido que FALHOU é fato objetivo: reprovação. Gate exigido que não pôde ser
        // apurado é ausência de verificação — e ausência nunca é aprovação (OPS-064).
        var verdict = string.Equals(report.ReasonCode, ReasonFailed, StringComparison.Ordinal)
            ? LayerVerdict.Fail
            : LayerVerdict.NotRun;

        return new LayerResult(layer.Layer, verdict, report.ReasonCode, report.Detail);
    }

    /// <summary>
    /// A evidência que vai ao revisor — o texto que substitui "o ator diz que não pôde executar".
    /// Ela é o motivo de esta correção existir: sem entregar o resultado real ao revisor, a camada
    /// determinística passaria e o parecer continuaria reprovando por falta de prova.
    /// </summary>
    public static string DescribeForReviewer(DeliveryGateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.NotApplicable)
        {
            return "(o card não exige gate executável)";
        }

        var builder = new StringBuilder();
        builder.Append("A PLATAFORMA executou os gates declarados no manifesto da própria entrega, ")
            .Append("numa worktree isolada, sem rede e com teto de tempo. Este é o resultado real; ")
            .Append("o ator não tem — e não precisa ter — permissão para executá-los.")
            .Append('\n').Append('\n')
            .Append("Resultado: ").Append(report.ReasonCode).Append(" — ").Append(report.Detail)
            .Append('\n');

        foreach (var outcome in report.Outcomes)
        {
            builder.Append('\n')
                .Append("### ").Append(outcome.Display)
                .Append(outcome.Passed ? " — PASSOU" : " — FALHOU")
                .Append(" (código ").Append(outcome.ExitCode).Append(")\n")
                .Append("```\n").Append(outcome.Output).Append("\n```\n");
        }

        if (report.Outcomes.Count == 0)
        {
            builder.Append('\n')
                .Append("NENHUM gate chegou a ser executado. Ausência de verificação não é ")
                .Append("aprovação: trate a evidência como insuficiente.");
        }

        return builder.ToString();
    }

    private sealed record ParsedManifest(HashSet<string> Scripts, bool HasExternalDependencies);

    private static ParsedManifest? ParseManifest(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty("scripts", out var scripts) ||
                scripts.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var script in scripts.EnumerateObject())
            {
                if (script.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(script.Value.GetString()))
                {
                    names.Add(script.Name);
                }
            }

            return names.Count == 0
                ? null
                : new ParsedManifest(names, HasDependencies(document.RootElement));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasDependencies(JsonElement root)
    {
        foreach (var section in (ReadOnlySpan<string>)["dependencies", "devDependencies"])
        {
            if (root.TryGetProperty(section, out var value) &&
                value.ValueKind == JsonValueKind.Object &&
                value.EnumerateObject().Any())
            {
                return true;
            }
        }

        return false;
    }
}
