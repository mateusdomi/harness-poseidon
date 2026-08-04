using System.Text.Json;
using Harness.Modules.Workflows.Product;

namespace Harness.Host.Product;

/// <summary>
/// Exercita a jornada da entrega com o POSEIDON no controle: ele compila, sobe o backend, sobe a
/// interface, põe um medidor entre os dois, dirige o navegador e só então lê o resultado.
///
/// A divisão de responsabilidade do §7, que é o ponto inteiro deste verificador: <b>o agente pode
/// escrever a especificação da jornada; o agente não aprova a própria jornada.</b> A spec vem da
/// entrega — é onde o critério de aceite virou passo executável, e a plataforma não tem como saber
/// o que "emprestar" significa naquele produto. Quem executa, quem escolhe contra qual aplicação, e
/// quem lê o veredito é o Poseidon.
///
/// Contra prova vazia, duas defesas independentes: a inspeção estrutural das specs
/// (<see cref="E2ESpecAnalysis"/>), que rejeita `expect(true)` sem navegação, e a exigência de que
/// os processos da aplicação continuem VIVOS ao final — um relatório verde produzido com o backend
/// morto no meio provou outra coisa.
/// </summary>
public sealed class PlaywrightJourneyVerifier(TrustedProcessRunner runner) : ProcessProductVerifier(runner)
{
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan JourneyTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Nome do relatório que o Poseidon manda o Playwright escrever. Fixo aqui, nunca vindo da entrega.</summary>
    private const string ReportFileName = ".poseidon-e2e-report.json";

    public override ProductEvidenceKind Kind => ProductEvidenceKind.E2EJourneyPassed;

    public override string Name => "playwright-journey";

    /// <summary>
    /// A jornada também prova a integração tela↔API — mas só quando o Poseidon MEDE o tráfego
    /// atravessando o próprio encaminhador. Declará-la aqui é o que faz o plano exigir prova
    /// controlada pelo Poseidon para esse requisito; a derivação em si continua condicionada ao
    /// fato observado, nunca à existência deste verificador.
    /// </summary>
    public override IReadOnlyList<ProductEvidenceKind> DerivedKinds =>
        [ProductEvidenceKind.FrontendBackendIntegration];

    public override bool AppliesTo(ProjectEffectiveProfile profile) => profile.Frontend.Required;

    public override async Task<ProductVerificationRecord?> VerifyAsync(
        ProductVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var frontend = FrontendLocator.LocateManifest(
            context.WorkspaceRoot, context.Profile.Frontend.Framework);
        if (frontend is null)
        {
            return Negative(
                context,
                $"Nenhum manifesto declarando {context.Profile.Frontend.Framework ?? "o framework do perfil"} " +
                "na entrega: não há interface para percorrer.");
        }

        var specs = LocateSpecs(context.WorkspaceRoot);
        var analysis = E2ESpecAnalysis.Analyze(specs);
        if (!analysis.Usable)
        {
            return Negative(context, analysis.Reason!);
        }

        var specRoot = SpecRoot(specs);
        var (apiProject, ambiguity) = WebSurfaceLocator.LocateAspNetCore(
            context.WorkspaceRoot, SafeFind(context.WorkspaceRoot, "*.csproj"));
        if (ambiguity is not null)
        {
            return Negative(context, ambiguity);
        }

        await using var api = apiProject is null
            ? null
            : await DeliveryApiSession.StartAsync(
                Runner, context.WorkspaceRoot, apiProject, ReadinessTimeout, cancellationToken);
        if (api is not null && !api.IsUp)
        {
            return Negative(context, $"O backend não subiu para a jornada: {api.Failure}", api.CommandLine);
        }

        await using var traffic = api is null ? null : ApiTrafficProbe.TryStart(api.BaseUrl!);
        var apiBaseForUi = traffic?.BaseUrl ?? api?.BaseUrl;

        await using var ui = await DeliveryFrontendSession.StartAsync(
            Runner, context.WorkspaceRoot, frontend.Value.Directory, frontend.Value.Content,
            apiBaseForUi, ReadinessTimeout, cancellationToken);
        if (!ui.IsUp)
        {
            return Negative(context, $"A interface não subiu para a jornada: {ui.Failure}", ui.CommandLine);
        }

        var reportPath = Path.Combine(context.WorkspaceRoot, ReportFileName);
        DeleteIfPresent(reportPath);

        var result = await Runner.RunAsync(
            "npx",
            ["playwright", "test", "--reporter=json"],
            context.WorkspaceRoot,
            specRoot,
            JourneyTimeout,
            cancellationToken,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PLAYWRIGHT_JSON_OUTPUT_NAME"] = reportPath,
                ["PLAYWRIGHT_BASE_URL"] = ui.BaseUrl!,
                ["BASE_URL"] = ui.BaseUrl!,
                ["E2E_BASE_URL"] = ui.BaseUrl!,
                ["API_BASE_URL"] = apiBaseForUi ?? string.Empty,
                ["PW_TEST_HTML_REPORT_OPEN"] = "never",
            });

        var report = ReadReport(reportPath);
        DeleteIfPresent(reportPath);

        // Os processos precisam ter sobrevivido à jornada. Um relatório verde com a aplicação morta
        // no meio significa que os casos que "passaram" rodaram contra outra coisa.
        var apiAlive = api is null || api.Process is { IsRunning: true };
        var uiAlive = ui.Process is { IsRunning: true };

        var command = $"npx playwright test --reporter=json (em {specRoot})";
        if (result.Outcome != VerificationOutcomeKind.Passed)
        {
            return Negative(
                context,
                $"A jornada reprovou: {Describe(result)}. " +
                $"{Summarize(report, analysis, traffic)}",
                command,
                result.ExitCode);
        }

        if (report is null)
        {
            return Negative(
                context,
                "O Playwright saiu com zero mas não produziu relatório: sem relatório não há como " +
                "distinguir jornada executada de suíte vazia.",
                command);
        }

        if (report.Executed <= 0)
        {
            return Negative(
                context,
                $"Nenhum caso de jornada foi executado ({report.Skipped} pulados). " +
                "Suíte que não roda nada sai com zero e não prova nada.",
                command);
        }

        if (!apiAlive || !uiAlive)
        {
            return Negative(
                context,
                $"A jornada terminou verde mas {(apiAlive ? "a interface" : "o backend")} não " +
                "estava mais no ar ao final: o que passou não rodou contra a aplicação entregue.",
                command);
        }

        var derived = DeriveIntegration(context, traffic, command);
        return new ProductVerificationRecord(
            Kind,
            true,
            Name,
            command,
            0,
            context.CommitSha,
            DateTimeOffset.UtcNow,
            context.AttemptId,
            specRoot,
            $"Jornada executada pelo Poseidon contra a aplicação da entrega: {report.Executed} casos " +
            $"aprovados, {report.Skipped} pulados, {analysis.Navigations} navegações e " +
            $"{analysis.Assertions} asserções com conteúdo nas especificações. " +
            (traffic is null
                ? "Sem backend na entrega: tráfego de API não medido."
                : $"{traffic.Requests} chamadas da interface atravessaram até a API."),
            VerificationTrustLevel.PoseidonControlled,
            derived);
    }

    /// <summary>
    /// A integração tela↔API é derivada SOMENTE quando o Poseidon contou tráfego real atravessando
    /// o medidor. Sem tráfego, ela não é derivada — e o §11 é explícito: não inferir em silêncio.
    /// A ausência aqui é honesta, porque uma interface que não chamou a API durante a jornada
    /// principal pode estar renderizando dado embutido, que é exatamente o que se quer pegar.
    /// </summary>
    private ProductVerificationRecord[] DeriveIntegration(
        ProductVerificationContext context, ApiTrafficProbe? traffic, string command)
    {
        if (traffic is null || traffic.Requests == 0)
        {
            return [];
        }

        var reachedApi = traffic.Requests - traffic.UpstreamFailures;
        return
        [
            new ProductVerificationRecord(
                ProductEvidenceKind.FrontendBackendIntegration,
                reachedApi > 0,
                Name + ":traffic",
                command,
                reachedApi > 0 ? 0 : -1,
                context.CommitSha,
                DateTimeOffset.UtcNow,
                context.AttemptId,
                ".",
                reachedApi > 0
                    ? $"Durante a jornada, {reachedApi} de {traffic.Requests} chamadas da interface " +
                      "chegaram à API pelo encaminhador do Poseidon: a tela consome o backend."
                    : $"As {traffic.Requests} chamadas da interface não chegaram à API " +
                      $"({traffic.UpstreamFailures} falhas de encaminhamento).",
                VerificationTrustLevel.PoseidonControlled),
        ];
    }

    private static List<(string File, string Content)> LocateSpecs(string workspaceRoot)
    {
        var found = new List<(string File, string Content)>();
        foreach (var pattern in (string[])["*.spec.ts", "*.spec.tsx", "*.spec.js", "*.spec.mjs"])
        {
            foreach (var relative in FileDiscovery.Find(workspaceRoot, pattern))
            {
                var absolute = Path.Combine(
                    workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    var content = File.ReadAllText(absolute);

                    // Só specs de Playwright: um teste unitário de componente casa com `*.spec.ts`
                    // e não é jornada — contá-lo faria a suíte de unidade satisfazer o E2E.
                    if (content.Contains("@playwright/test", StringComparison.Ordinal))
                    {
                        found.Add((relative, content));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Arquivo ilegível é arquivo ausente para efeito de descoberta.
                }
            }
        }

        return found;
    }

    /// <summary>O diretório de onde o Playwright é invocado: o ancestral comum das specs.</summary>
    private static string SpecRoot(IReadOnlyList<(string File, string Content)> specs)
    {
        var directories = specs
            .Select(spec => spec.File.Contains('/', StringComparison.Ordinal)
                ? spec.File[..spec.File.LastIndexOf('/')]
                : ".")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (directories.Count == 1)
        {
            var single = directories[0];

            // `tests/` e `e2e/` são pastas de teste, não raízes de projeto: o `playwright.config`
            // vive um nível acima delas.
            var leaf = single.Contains('/', StringComparison.Ordinal)
                ? single[(single.LastIndexOf('/') + 1)..]
                : single;
            return leaf is "tests" or "test" or "e2e" or "specs"
                ? (single.Contains('/', StringComparison.Ordinal) ? single[..single.LastIndexOf('/')] : ".")
                : single;
        }

        return ".";
    }

    private sealed record JourneyReport(int Executed, int Skipped, int Unexpected);

    /// <summary>
    /// Lê o relatório que o Poseidon mandou escrever. Relatório ilegível não vira aprovação: quem
    /// não consegue ler o resultado não sabe o resultado.
    /// </summary>
    private static JourneyReport? ReadReport(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("stats", out var stats) ||
                stats.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new JourneyReport(
                Number(stats, "expected"),
                Number(stats, "skipped"),
                Number(stats, "unexpected"));
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static string Summarize(
        JourneyReport? report, E2ESpecVerdict analysis, ApiTrafficProbe? traffic) =>
        report is null
            ? $"Sem relatório; especificações declaravam {analysis.Tests} casos."
            : $"{report.Executed} aprovados, {report.Unexpected} reprovados, {report.Skipped} pulados" +
              (traffic is null ? "." : $"; {traffic.Requests} chamadas de API.");

    private static string Describe(TrustedProcessResult result) => result.Outcome switch
    {
        VerificationOutcomeKind.TimedOut => "estourou o tempo limite",
        VerificationOutcomeKind.InfrastructureError => result.Output,
        _ => $"exit {result.ExitCode}\n{result.Output}",
    };

    private static void DeleteIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Relatório antigo que não sai do lugar é tratado na leitura: o conteúdo é conferido,
            // não a existência.
        }
    }

    private ProductVerificationRecord Negative(
        ProductVerificationContext context, string reason, string? command = null, int exitCode = -1) =>
        new(Kind, false, Name, command ?? "playwright-journey", exitCode, context.CommitSha,
            DateTimeOffset.UtcNow, context.AttemptId, ".", reason,
            VerificationTrustLevel.PoseidonControlled);
}
