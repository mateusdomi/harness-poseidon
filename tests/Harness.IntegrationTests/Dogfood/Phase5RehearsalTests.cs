using System.Globalization;
using Harness.Host.Product;
using Harness.IntegrationTests.Product;
using Harness.Modules.Workflows.Product;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.IntegrationTests.Dogfood;

/// <summary>
/// ENSAIO da fase de Desenvolvimento — o caminho do CONTROL PLANE, com bancos reais, árvore de
/// entrega real e os verificadores canônicos.
///
/// <b>O que este arquivo NÃO é.</b> Não é o Golden Run e não prova que agentes escrevem software.
/// Nenhum modelo é invocado aqui: a "entrega" é escrita pelo teste, deterministicamente, como um
/// executor falso escreveria. O que se está medindo é o encanamento — perfil materializado, plano
/// derivado, verificação executada, evidência colhida, portão decidido, ledger gravado, recibo
/// ligado — e se ele atravessa de ponta a ponta sem furo e sem duplicar em caso de reinício.
///
/// A distinção precisa aparecer no relatório com todas as letras, porque confundir as duas coisas
/// seria exatamente o tipo de afirmação que este subsistema inteiro existe para impedir:
///
/// <code>
/// Control-plane Phase 5 path = o que este arquivo mede
/// AI software development    = NÃO TESTADO
/// </code>
/// </summary>
[Collection(ProcessVerificationGroup.Name)]
public sealed class Phase5RehearsalTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-04T12:00:00Z", CultureInfo.InvariantCulture);

    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5T00";
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5P00";
    private const string Attempt = "01ARZ3NDEKTSV4RRFFQ69G5A00";
    private const string Card = "01ARZ3NDEKTSV4RRFFQ69G5C00";
    private const string Commit = "dddd000011112222333344445555666677778888";
    private const string Pedido = "Crie somente uma API de empréstimos de livros.";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poseidon-ensaio-{Guid.NewGuid():N}");

    public Phase5RehearsalTests() => Directory.CreateDirectory(_root);

    /// <summary>Caso 1 — a fase de Desenvolvimento entra e SAI corretamente.</summary>
    [Fact]
    public async Task Caso1EntregaCompletaAtravessaOCaminhoDeDesenvolvimentoEAbreOPortao()
    {
        EscreverEntregaCompleta();
        await using var ambiente = await Ambiente.CriarAsync(_root);

        var outcome = await ambiente.Evaluator.EvaluateAsync(
            Tenant, Project, _root, Commit, ProductDeliveryEvaluator.DevelopmentPhaseOrder,
            CancellationToken.None, Pedido, Attempt, Card);

        Assert.NotNull(outcome.Verdict);
        Assert.True(outcome.Verdict.Satisfied, Diagnostico(outcome));
        Assert.Null(outcome.Failure);

        // O perfil foi MATERIALIZADO no caminho, não pressuposto.
        Assert.NotNull(outcome.Profile);
        Assert.Equal(nameof(ProductModality.ApiOnly), outcome.Profile.Modality);

        // O plano derivado não tem buraco: nenhum requisito exigido ficou sem verificador.
        Assert.NotNull(outcome.Plan);
        Assert.Empty(outcome.Plan.Unsupported);

        // O ledger recebeu o conjunto que decidiu o portão.
        Assert.NotNull(outcome.EvidenceSetId);
        var sets = await ambiente.EvidenceSets.ListAsync(Tenant, Project, 10, CancellationToken.None);
        Assert.Single(sets);
        Assert.Equal(Commit, sets[0].CommitSha);
        Assert.Equal("satisfied", sets[0].GateDecision);
    }

    /// <summary>Caso 2 — a implementação falha: o portão REPROVA e diz o que faltou.</summary>
    [Fact]
    public async Task Caso2ImplementacaoIncompletaSeguraOPortaoComMotivoNomeado()
    {
        // Backend que não compila: tudo o mais existe, e o que falha é o fato verificável.
        EscreverEntregaCompleta(compila: false);
        await using var ambiente = await Ambiente.CriarAsync(_root);

        var outcome = await ambiente.Evaluator.EvaluateAsync(
            Tenant, Project, _root, Commit, ProductDeliveryEvaluator.DevelopmentPhaseOrder,
            CancellationToken.None, Pedido, Attempt, Card);

        Assert.Equal(ProductDeliveryFailures.DeliveryIncomplete, outcome.Failure);
        Assert.NotNull(outcome.Verdict);
        Assert.False(outcome.Verdict.Satisfied);
        Assert.Contains(
            outcome.Verdict.Findings,
            finding => finding.Kind == ProductEvidenceKind.BackendBuild &&
                finding.Gap == ProductEvidenceGap.Failed);

        // A tentativa que REPROVOU também fica no ledger: é o que a fábrica precisa para aprender,
        // e o que desaparecia quando só a última tentativa era guardada.
        var sets = await ambiente.EvidenceSets.ListAsync(Tenant, Project, 10, CancellationToken.None);
        Assert.Single(sets);
        Assert.Equal("failed", sets[0].GateDecision);
    }

    /// <summary>
    /// Caso 4 — REINÍCIO no meio. Um segundo avaliador, sobre os mesmos bancos, retoma sem
    /// duplicar: o perfil continua na mesma versão (resolver duas vezes a mesma decisão não cria
    /// versão nova) e o ledger ganha a segunda avaliação como fato novo, que é o comportamento
    /// correto de um registro append-only.
    /// </summary>
    [Fact]
    public async Task Caso4ReinicioRetomaSemDuplicarOPerfil()
    {
        EscreverEntregaCompleta();
        await using var ambiente = await Ambiente.CriarAsync(_root);

        var primeiro = await ambiente.Evaluator.EvaluateAsync(
            Tenant, Project, _root, Commit, ProductDeliveryEvaluator.DevelopmentPhaseOrder,
            CancellationToken.None, Pedido, Attempt, Card);

        // "Reinício": instância nova do avaliador sobre o MESMO estado durável.
        var segundo = await ambiente.NovoAvaliador().EvaluateAsync(
            Tenant, Project, _root, Commit, ProductDeliveryEvaluator.DevelopmentPhaseOrder,
            CancellationToken.None, Pedido, Attempt, Card);

        Assert.NotNull(primeiro.Profile);
        Assert.NotNull(segundo.Profile);
        Assert.Equal(primeiro.Profile.Version, segundo.Profile.Version);
        Assert.Equal(primeiro.Profile.Fingerprint, segundo.Profile.Fingerprint);
        Assert.True(segundo.Verdict?.Satisfied, Diagnostico(segundo));
        Assert.NotEqual(primeiro.EvidenceSetId, segundo.EvidenceSetId);
    }

    /// <summary>
    /// A trilha de auditoria completa, do recibo até o commit — a pergunta que o §18 da missão
    /// anterior exigiu que fosse navegável.
    /// </summary>
    [Fact]
    public async Task ORecibaDoTurnoPassaAApontarParaOConjuntoQueDecidiuOPortao()
    {
        EscreverEntregaCompleta();
        await using var ambiente = await Ambiente.CriarAsync(_root);
        await ambiente.CriarReciboDoTurnoAsync();

        var outcome = await ambiente.Evaluator.EvaluateAsync(
            Tenant, Project, _root, Commit, ProductDeliveryEvaluator.DevelopmentPhaseOrder,
            CancellationToken.None, Pedido, Attempt, Card);

        var receipt = await ambiente.Governance.GetReceiptAsync(
            Tenant, Ambiente.TurnId, CancellationToken.None);

        Assert.NotNull(receipt);
        Assert.Equal(outcome.EvidenceSetId, receipt.Context?.EvidenceSetId);
        Assert.Equal(Commit, receipt.Context?.EvidenceCommitSha);
        Assert.StartsWith("product_dod_satisfied", receipt.Context?.GateDecision ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// A telemetria de progresso vista de ponta a ponta: uma tentativa que reprova, outra que
    /// corrige, e o medidor mostrando quantas lacunas fecharam. É o dado que faltava para
    /// distinguir uma fábrica que converge de uma que gira.
    ///
    /// Nada disto altera comportamento: o portão decide igual com e sem a métrica.
    /// </summary>
    [Fact]
    public async Task ATelemetriaMostraQuantasLacunasFecharamEntreDuasTentativas()
    {
        EscreverEntregaCompleta(compila: false);
        await using var ambiente = await Ambiente.CriarAsync(_root);

        var reprovada = await ambiente.Evaluator.EvaluateAsync(
            Tenant, Project, _root, Commit, ProductDeliveryEvaluator.DevelopmentPhaseOrder,
            CancellationToken.None, Pedido, Attempt, Card);

        Assert.NotNull(reprovada.Progress);
        Assert.True(reprovada.Progress.IsBaseline, "a primeira avaliação é linha de base, não progresso.");
        Assert.True(reprovada.Progress.After > 0);

        // O "agente" corrige a implementação e entrega de novo.
        EscreverEntregaCompleta();
        var corrigida = await ambiente.NovoAvaliador().EvaluateAsync(
            Tenant, Project, _root, Commit, ProductDeliveryEvaluator.DevelopmentPhaseOrder,
            CancellationToken.None, Pedido, Attempt, Card);

        Assert.NotNull(corrigida.Progress);
        Assert.Equal(reprovada.Progress.After, corrigida.Progress.Before);
        Assert.Equal(0, corrigida.Progress.After);
        Assert.Equal(reprovada.Progress.After, corrigida.Progress.Resolved);
        Assert.Equal(0, corrigida.Progress.Repeated);
        Assert.Equal(0, corrigida.Progress.New);
        Assert.True(corrigida.Progress.Score > 0, corrigida.Progress.Summary());
    }

    private static string Diagnostico(ProductDeliveryEvaluator.Outcome outcome) =>
        outcome.Verdict is null
            ? $"sem veredito; falha={outcome.Failure}"
            : outcome.Verdict.Summary() + "\n" + string.Join(
                '\n', outcome.Verdict.Findings.Select(finding => $"  {finding.Kind}: {finding.Reason}"));

    /// <summary>
    /// O ambiente durável do ensaio: bancos reais, migrations aplicadas, verificadores canônicos.
    /// Nada de dublê de persistência — o que se quer provar é o encanamento, e um dublê provaria
    /// o dublê.
    /// </summary>
    private sealed class Ambiente : IAsyncDisposable
    {
        public const string TurnId = "01ARZ3NDEKTSV4RRFFQ69G5R00";

        private readonly SqliteWriteDispatcher _dispatcher;
        private readonly string _root;

        private Ambiente(SqliteWriteDispatcher dispatcher, string root)
        {
            _dispatcher = dispatcher;
            _root = root;
            Profiles = new SqliteProjectEffectiveProfileStore(dispatcher);
            EvidenceSets = new SqliteProductEvidenceSetStore(dispatcher);
            Governance = new SqliteGovernanceRuntimeStore(dispatcher);
            Evaluator = NovoAvaliador();
        }

        public SqliteProjectEffectiveProfileStore Profiles { get; }

        public SqliteProductEvidenceSetStore EvidenceSets { get; }

        public SqliteGovernanceRuntimeStore Governance { get; }

        public ProductDeliveryEvaluator Evaluator { get; }

        public static async Task<Ambiente> CriarAsync(string root)
        {
            var database = Path.Combine(root, ".poseidon-ensaio.db");
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(database);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, CancellationToken.None);
            return new Ambiente(dispatcher, root);
        }

        public ProductDeliveryEvaluator NovoAvaliador() =>
            new(
                Profiles,
                new FixedClock(Now),
                null,
                new ProductVerificationRunner(
                    ProductVerifierCatalog.CreateAll(new TrustedProcessRunner())),
                null,
                EvidenceSets,
                Governance);

        public Task<GovernanceTurnReceiptRecord> CriarReciboDoTurnoAsync() =>
            Governance.CreateReceiptAsync(
                new GovernanceTurnReceiptCreateCommand(
                    Tenant, Project, Card, Attempt, TurnId, "worker-dev", "1.0.0",
                    [new GovernanceReceiptDocumentRecord(
                        "governance-core", "sha256:" + new string('a', 64), "always", "Always", 900)],
                    900, [], [], 0, "fake", "fake-model", Now, new string('b', 64)),
                CancellationToken.None);

        public async ValueTask DisposeAsync() => await _dispatcher.DisposeAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    /// <summary>
    /// A "entrega" que um executor determinístico produziria: API .NET que compila, suíte de
    /// testes, migrations, runbook e os scripts convencionados. É API-only de propósito — a
    /// modalidade Web exigiria interface e jornada de navegador, que dependem de rede.
    /// </summary>
    private void EscreverEntregaCompleta(bool compila = true)
    {
        var api = Path.Combine(_root, "src", "Emprestimos.Api");
        Directory.CreateDirectory(api);
        File.WriteAllText(
            Path.Combine(api, "Emprestimos.Api.csproj"), Csproj("Microsoft.NET.Sdk.Web"));
        File.WriteAllText(
            Path.Combine(api, "Program.cs"),
            compila
                ? string.Join(
                    '\n',
                    "var builder = WebApplication.CreateBuilder(args);",
                    "var app = builder.Build();",
                    "app.MapGet(\"/\", () => \"ok\");",
                    "app.MapGet(\"/openapi/v1.json\", () => Results.Text(Contrato, \"application/json\"));",
                    "app.MapGet(\"/emprestimos\", () => Results.Json(Ler()));",
                    "app.MapPost(\"/emprestimos\", (Dictionary<string, object?> corpo) =>",
                    "{",
                    "    var itens = Ler();",
                    "    corpo[\"id\"] = itens.Count + 1;",
                    "    itens.Add(corpo);",
                    "    File.WriteAllText(Caminho(), System.Text.Json.JsonSerializer.Serialize(itens));",
                    "    return Results.Json(corpo);",
                    "});",
                    "app.Run();",
                    "",
                    "static List<Dictionary<string, object?>> Ler() =>",
                    "    File.Exists(Caminho())",
                    "        ? System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(",
                    "            File.ReadAllText(Caminho())) ?? new()",
                    "        : new();",
                    "",
                    "static string Caminho() => Path.Combine(AppContext.BaseDirectory, \"emprestimos.json\");",
                    "",
                    "public partial class Program",
                    "{",
                    "    public const string Contrato = \"" + Contrato() + "\";",
                    "}")
                : "Console.WriteLine(\"não compila\"  //");

        var tests = Path.Combine(_root, "tests", "Emprestimos.Tests");
        Directory.CreateDirectory(tests);
        File.WriteAllText(
            Path.Combine(tests, "Emprestimos.Tests.csproj"), Csproj("Microsoft.NET.Sdk", exe: true));
        File.WriteAllText(
            Path.Combine(tests, "Program.cs"),
            "if (1 + 1 != 2) { throw new Exception(\"aritmética quebrada\"); }\n");

        File.WriteAllText(
            Path.Combine(_root, "Emprestimos.slnx"),
            "<Solution>\n" +
            "  <Project Path=\"src/Emprestimos.Api/Emprestimos.Api.csproj\" />\n" +
            "  <Project Path=\"tests/Emprestimos.Tests/Emprestimos.Tests.csproj\" />\n" +
            "</Solution>\n");

        var migrations = Path.Combine(_root, "src", "Emprestimos.Api", "Migrations");
        Directory.CreateDirectory(migrations);
        File.WriteAllText(
            Path.Combine(migrations, "0001_inicial.sql"),
            "CREATE TABLE emprestimos (id INTEGER PRIMARY KEY, titulo TEXT NOT NULL);\n");

        File.WriteAllText(
            Path.Combine(_root, "README.md"),
            "# Empréstimos\n\n## Como executar\n\n    dotnet run --project src/Emprestimos.Api\n");
    }

    private static string Contrato() =>
        ("{\"openapi\":\"3.0.1\",\"info\":{\"title\":\"emprestimos\",\"version\":\"1.0\"}," +
         "\"paths\":{\"/emprestimos\":{" +
         "\"get\":{\"responses\":{\"200\":{\"description\":\"ok\"}}}," +
         "\"post\":{\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{" +
         "\"type\":\"object\",\"required\":[\"titulo\"]," +
         "\"properties\":{\"titulo\":{\"type\":\"string\"}}}}}}," +
         "\"responses\":{\"200\":{\"description\":\"ok\"}}}}}}").Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Csproj(string sdk, bool exe = false) =>
        $"""
         <Project Sdk="{sdk}">
           <PropertyGroup>
             <TargetFramework>net8.0</TargetFramework>
             <Nullable>enable</Nullable>
             <ImplicitUsings>enable</ImplicitUsings>
             {(exe ? "<OutputType>Exe</OutputType>" : string.Empty)}
             <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
             <RunAnalyzers>false</RunAnalyzers>
           </PropertyGroup>
         </Project>
         """;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Sobra de árvore temporária não é falha de ensaio.
        }
    }
}
