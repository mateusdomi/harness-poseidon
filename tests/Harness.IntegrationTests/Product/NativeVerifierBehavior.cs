using Harness.Host.Product;
using Harness.Modules.Workflows.Application;
using Harness.Modules.Workflows.Product;

namespace Harness.IntegrationTests.Product;

/// <summary>
/// Os verificadores NATIVOS exercitados contra aplicações de verdade: compiladas, iniciadas em
/// porta livre, consultadas por HTTP e derrubadas ao final.
///
/// Nada aqui é simulado. Cada cenário escreve uma aplicação ASP.NET Core mínima em disco, e o
/// verificador faz exatamente o que faria numa entrega da frota. As aplicações usam só o framework
/// compartilhado do SDK — nenhum pacote externo — porque o ambiente da verificação roda sem rede,
/// e um teste que dependesse de restauração remota mediria a conectividade da máquina.
/// </summary>
[Collection(ProcessVerificationGroup.Name)]
public sealed class NativeVerifierBehavior : IDisposable
{
    private const string Commit = "cccc000011112222333344445555666677778888";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poseidon-nativo-{Guid.NewGuid():N}");

    public NativeVerifierBehavior() => Directory.CreateDirectory(_root);

    // ---------- OpenAPI ----------

    [Fact]
    public async Task OContratoRealEBuscadoDaAplicacaoNoAr()
    {
        EscreverApi(Program(contrato: true, persistencia: Persistencia.Arquivo));

        var record = await new OpenApiNativeVerifier(new TrustedProcessRunner())
            .VerifyAsync(Contexto(Web()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.True(record.Succeeded, record.Detail);
        Assert.Equal(VerificationTrustLevel.PoseidonControlled, record.Trust);
        Assert.Contains("sha256", record.Detail!, StringComparison.Ordinal);
        Assert.Contains("operações", record.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fraude do §5, exercitada: a entrega declara um script `openapi` que só sai com zero, e não
    /// publica contrato nenhum. O verificador nativo não olha para o script — ele pergunta à
    /// aplicação, e a aplicação não tem o que responder.
    /// </summary>
    [Fact]
    public async Task ScriptQueSaiComZeroNaoSubstituiContratoPublicado()
    {
        EscreverApi(Program(contrato: false, persistencia: Persistencia.Nenhuma));
        File.WriteAllText(
            Path.Combine(_root, "package.json"),
            """
            {
              "name": "fraude",
              "scripts": { "openapi": "node -e \"process.exit(0)\"" }
            }
            """);

        var record = await new OpenApiNativeVerifier(new TrustedProcessRunner())
            .VerifyAsync(Contexto(Web()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.False(record.Succeeded);
        Assert.Contains("não publicou contrato", record.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContratoSemNenhumaOperacaoNaoEContrato()
    {
        EscreverApi(Program(contrato: true, persistencia: Persistencia.Nenhuma, contratoVazio: true));

        var record = await new OpenApiNativeVerifier(new TrustedProcessRunner())
            .VerifyAsync(Contexto(Web()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.False(record.Succeeded);
        Assert.Contains("nenhuma operação", record.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuasAplicacoesWebSemFormaDeEscolherProduzemAmbiguidade()
    {
        EscreverApi(Program(contrato: true, persistencia: Persistencia.Nenhuma));
        EscreverApi(Program(contrato: true, persistencia: Persistencia.Nenhuma), pasta: "src/Outra.Api");

        var record = await new OpenApiNativeVerifier(new TrustedProcessRunner())
            .VerifyAsync(Contexto(Web()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.False(record.Succeeded);
        Assert.Contains(WebSurfaceLocator.Ambiguous, record.Detail!, StringComparison.Ordinal);
    }

    // ---------- Persistência ----------

    /// <summary>
    /// A prova que separa persistir de guardar em memória: escrever, ler, DERRUBAR a aplicação,
    /// subir de novo e achar o dado lá.
    /// </summary>
    [Fact]
    public async Task ODadoSobreviveAoReinicioQuandoAAplicacaoPersisteDeVerdade()
    {
        EscreverApi(Program(contrato: true, persistencia: Persistencia.Arquivo));

        var record = await new PersistenceNativeVerifier(new TrustedProcessRunner())
            .VerifyAsync(Contexto(Web()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.True(record.Succeeded, record.Detail);
        Assert.Equal(VerificationTrustLevel.PoseidonControlled, record.Trust);
        Assert.Contains("subida de novo", record.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// O caso que o requisito existe para pegar: migrations impecáveis, API que aceita e devolve, e
    /// uma lista estática por trás. Passa em tudo até o primeiro reinício.
    /// </summary>
    [Fact]
    public async Task AplicacaoQueGuardaEmMemoriaReprovaDepoisDoReinicio()
    {
        EscreverApi(Program(contrato: true, persistencia: Persistencia.Memoria));

        var record = await new PersistenceNativeVerifier(new TrustedProcessRunner())
            .VerifyAsync(Contexto(Web()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.False(record.Succeeded);
        Assert.Contains("desapareceu depois de reiniciar", record.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// O §13 em ação: configuração apontando para um servidor que não é local interrompe a
    /// verificação. Não verificar é ruim; escrever num banco que pode ser real não tem preço.
    /// </summary>
    [Fact]
    public async Task ConexaoQueNaoEComprovadamenteLocalInterrompeAVerificacao()
    {
        EscreverApi(Program(contrato: true, persistencia: Persistencia.Arquivo));
        File.WriteAllText(
            Path.Combine(_root, "src", "Emprestimos.Api", "appsettings.json"),
            """
            { "ConnectionStrings": { "Default": "Server=db.producao.interno;Database=Emprestimos;" } }
            """);

        var record = await new PersistenceNativeVerifier(new TrustedProcessRunner())
            .VerifyAsync(Contexto(Web()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.False(record.Succeeded);
        Assert.Contains(nameof(VerificationOutcomeKind.NotSupported), record.Detail!, StringComparison.Ordinal);
        Assert.Contains("não é comprovadamente local", record.Detail!, StringComparison.Ordinal);
    }

    // ---------- Segurança ----------

    [Fact]
    public async Task SegredoEmCodigoNaEntregaReprovaAVerificacaoDeSeguranca()
    {
        EscreverApi(Program(contrato: true, persistencia: Persistencia.Nenhuma));
        File.WriteAllText(
            Path.Combine(_root, "src", "Emprestimos.Api", "Segredos.cs"),
            "// token = \"ghp_" + new string('A', 36) + "\"\n");

        var record = await new SecurityBaselineVerifier(new TrustedProcessRunner())
            .VerifyAsync(Contexto(Web()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.False(record.Succeeded);
        Assert.Contains("Segredo em código", record.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntregaLimpaPassaNaVerificacaoDeSegurancaComVarreduraRegistrada()
    {
        EscreverApi(Program(contrato: true, persistencia: Persistencia.Nenhuma));

        var record = await new SecurityBaselineVerifier(new TrustedProcessRunner())
            .VerifyAsync(Contexto(Web()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.True(record.Succeeded, record.Detail);
        Assert.Contains("Varredura de segredo limpa", record.Detail!, StringComparison.Ordinal);
        Assert.Equal(VerificationTrustLevel.PoseidonControlled, record.Trust);
    }

    // ---------- Jornada ----------

    /// <summary>
    /// O caminho negativo do verificador de jornada que dá para exercitar sem navegador: a entrega
    /// declara jornada vazia. A execução completa (backend + interface + Playwright) exige pacotes e
    /// navegadores baixados da rede, e o ambiente de verificação roda offline de propósito.
    /// </summary>
    [Fact]
    public async Task JornadaTrivialmenteVerdadeiraNaoSatisfazORequisito()
    {
        EscreverApi(Program(contrato: true, persistencia: Persistencia.Nenhuma));
        EscreverFrontendReact();
        Directory.CreateDirectory(Path.Combine(_root, "e2e"));
        File.WriteAllText(
            Path.Combine(_root, "e2e", "jornada.spec.ts"),
            """
            import { test, expect } from '@playwright/test';
            test("ok", async () => { expect(true).toBe(true); });
            """);

        var record = await new PlaywrightJourneyVerifier(new TrustedProcessRunner())
            .VerifyAsync(Contexto(Web()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.False(record.Succeeded);
        Assert.Contains("Nenhuma navegação", record.Detail!, StringComparison.Ordinal);
    }

    // ---------- o cenário C do §21, com o registro canônico ----------

    /// <summary>
    /// A entrega declara os três scripts convencionados, todos saindo com zero, e não publica
    /// contrato, não persiste e não tem jornada. Com os verificadores nativos registrados, o script
    /// PARA DE SER CONSULTADO: quem responde é a aplicação, e ela não tem o que mostrar.
    ///
    /// É a prova de que registrar um verificador nativo eleva a barra do requisito sozinho, sem
    /// tocar no portão — e de que exit zero deixou de ser moeda.
    /// </summary>
    [Fact]
    public async Task ScriptsFalsosNaoSatisfazemRequisitoQueExigeProvaDoPoseidon()
    {
        EscreverApi(Program(contrato: false, persistencia: Persistencia.Nenhuma));
        EscreverFrontendReact();
        File.WriteAllText(
            Path.Combine(_root, "package.json"),
            """
            {
              "name": "fraude",
              "scripts": {
                "openapi": "node -e \"process.exit(0)\"",
                "test:persistence": "node -e \"process.exit(0)\"",
                "test:e2e": "node -e \"process.exit(0)\"",
                "test:integration": "node -e \"process.exit(0)\""
              }
            }
            """);

        var runner = new ProductVerificationRunner(
            ProductVerifierCatalog.CreateAll(new TrustedProcessRunner()));
        var profile = Web();
        var plan = ProductVerificationPlan.From(
            profile, runner.NativeVerifiers, runner.ProjectControlledVerifiers);

        var records = await runner.RunAsync(Contexto(profile), plan, CancellationToken.None);

        // Nenhum dos três requisitos críticos foi satisfeito, e nenhum registro veio de script.
        foreach (var kind in (ProductEvidenceKind[])
        [
            ProductEvidenceKind.OpenApiGenerated,
            ProductEvidenceKind.PersistenceVerified,
            ProductEvidenceKind.E2EJourneyPassed,
        ])
        {
            Assert.DoesNotContain(records, record =>
                record.Kind == kind && record.Verifier.StartsWith("npm-script", StringComparison.Ordinal));
            Assert.DoesNotContain(records, record => record.Kind == kind && record.Succeeded);
        }

        var workspace = new FileSystemProductWorkspace(_root, Commit);
        var evidence = new ProductEvidenceCollectorPipeline().Collect(profile, workspace, records);
        var verdict = ProductDeliveryGate.Evaluate(profile, evidence.Items, Commit, plan);

        Assert.False(verdict.Satisfied);
        Assert.Contains(verdict.Findings, f => f.Kind == ProductEvidenceKind.OpenApiGenerated);
        Assert.Contains(verdict.Findings, f => f.Kind == ProductEvidenceKind.E2EJourneyPassed);
    }

    // ---------- fixtures ----------

    private enum Persistencia
    {
        Nenhuma,
        Memoria,
        Arquivo,
    }

    private ProductVerificationContext Contexto(ProjectEffectiveProfile profile) =>
        new(profile, _root, Commit, "projeto", "tentativa", "card");

    private static ProjectEffectiveProfile Web() => EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs("projeto", "Crie um sistema web de empréstimos.", []));

    private void EscreverApi(string program, string pasta = "src/Emprestimos.Api")
    {
        var directory = Path.Combine(_root, pasta.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var name = Path.GetFileName(directory);
        File.WriteAllText(
            Path.Combine(directory, $"{name}.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
                <RunAnalyzers>false</RunAnalyzers>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Program.cs"), program);
    }

    /// <summary>
    /// Uma API mínima de verdade. O contrato é servido pela própria aplicação (sem Swashbuckle, que
    /// exigiria rede), e a persistência é um arquivo JSON na worktree — o verificador não conhece
    /// tecnologia de armazenamento, ele prova COMPORTAMENTO.
    /// </summary>
    private static string Program(bool contrato, Persistencia persistencia, bool contratoVazio = false)
    {
        var contratoJson = contratoVazio
            ? "{\"openapi\":\"3.0.1\",\"info\":{\"title\":\"api\",\"version\":\"1\"},\"paths\":{}}"
            : "{\"openapi\":\"3.0.1\",\"info\":{\"title\":\"emprestimos\",\"version\":\"1.0\"}," +
              "\"paths\":{\"/itens\":{" +
              "\"get\":{\"responses\":{\"200\":{\"description\":\"ok\"}}}," +
              "\"post\":{\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{" +
              "\"type\":\"object\",\"required\":[\"titulo\"]," +
              "\"properties\":{\"titulo\":{\"type\":\"string\"}}}}}}," +
              "\"responses\":{\"200\":{\"description\":\"ok\"}}}}}}";

        var openApi = contrato
            ? "app.MapGet(\"/openapi/v1.json\", () => Results.Text(Contrato, \"application/json\"));"
            : string.Empty;

        var store = persistencia switch
        {
            Persistencia.Arquivo =>
                "static List<Dictionary<string, object?>> Ler()\n" +
                "{\n" +
                "    var caminho = Caminho();\n" +
                "    return File.Exists(caminho)\n" +
                "        ? System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(\n" +
                "            File.ReadAllText(caminho)) ?? new()\n" +
                "        : new();\n" +
                "}\n" +
                "static void Gravar(List<Dictionary<string, object?>> itens) =>\n" +
                "    File.WriteAllText(Caminho(), System.Text.Json.JsonSerializer.Serialize(itens));\n" +
                "static string Caminho() => Path.Combine(AppContext.BaseDirectory, \"itens.json\");",
            // A entrega que o requisito existe para pegar: aceita, devolve, e guarda numa lista
            // estática que morre com o processo.
            Persistencia.Memoria =>
                "static List<Dictionary<string, object?>> Ler() => Memoria.Itens;\n" +
                "static void Gravar(List<Dictionary<string, object?>> itens) { }\n" +
                "public static class Memoria\n" +
                "{\n" +
                "    public static readonly List<Dictionary<string, object?>> Itens = new();\n" +
                "}",
            _ => string.Empty,
        };

        var rotas = persistencia == Persistencia.Nenhuma
            ? string.Empty
            : "app.MapGet(\"/itens\", () => Results.Json(Ler()));\n" +
              "app.MapPost(\"/itens\", (Dictionary<string, object?> corpo) =>\n" +
              "{\n" +
              "    var itens = Ler();\n" +
              "    corpo[\"id\"] = itens.Count + 1;\n" +
              "    itens.Add(corpo);\n" +
              "    Gravar(itens);\n" +
              "    return Results.Json(corpo);\n" +
              "});";

        return
            "var builder = WebApplication.CreateBuilder(args);\n" +
            "var app = builder.Build();\n" +
            "app.MapGet(\"/\", () => \"ok\");\n" +
            openApi + "\n" +
            rotas + "\n" +
            "app.Run();\n\n" +
            store + "\n\n" +
            "public partial class Program\n" +
            "{\n" +
            "    public const string Contrato = \"" + contratoJson.Replace("\"", "\\\"") + "\";\n" +
            "}\n";
    }

    private void EscreverFrontendReact()
    {
        var directory = Path.Combine(_root, "web");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "package.json"),
            """
            {
              "name": "web",
              "dependencies": { "react": "18.0.0" },
              "devDependencies": { "@playwright/test": "1.0.0" },
              "scripts": { "build": "echo build", "dev": "echo dev" }
            }
            """);
    }

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
            // Sobra de worktree temporária não é falha de teste.
        }
    }
}
