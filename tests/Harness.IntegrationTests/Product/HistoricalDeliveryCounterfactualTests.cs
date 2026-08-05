using Harness.Host.Product;
using Harness.Modules.Workflows.Application;
using Harness.Modules.Workflows.Product;

namespace Harness.IntegrationTests.Product;

/// <summary>
/// O CONTRAFACTUAL do primeiro run real.
///
/// Em 2026-08-02 o pedido "sistema simples para controlar empréstimos de equipamentos" entrou no
/// Poseidon. A fase 5 fechou em 2026-08-04T13:18 tendo entregue **só servidor** — os dois cards de
/// interface do plano original esgotaram o orçamento de esforço, escalaram e foram cancelados —, e
/// um ADR produzido DENTRO do run registrou a fronteira como "entrega backend-only". A interface só
/// nasceu depois, em cards criados às 13:36 e 13:43, com a fase de Testes já ativa.
///
/// A pergunta que este arquivo responde, e que nenhuma outra parte da suíte responde: <b>as Fases
/// 1–6C impediriam o mesmo desfecho?</b>
///
/// O método é deliberadamente desconfortável: pegar o pedido ORIGINAL, palavra por palavra, e a
/// árvore que o run produziu ATÉ o fechamento da fase 5 — sem interface —, e submeter os dois aos
/// mecanismos de hoje. Nada é ajustado para passar; a entrega histórica é reproduzida como foi.
/// </summary>
[Collection(ProcessVerificationGroup.Name)]
public sealed class HistoricalDeliveryCounterfactualTests : IDisposable
{
    /// <summary>
    /// O pedido, exatamente como chegou à chefe em 2026-08-02T21:02:50 — inclusive sem acentos.
    /// Reescrevê-lo com o que hoje sabemos que teria funcionado melhor invalidaria o experimento.
    /// </summary>
    private const string PedidoOriginal =
        "Quero criar um sistema simples para controlar emprestimos de equipamentos. " +
        "Quero saber quem pegou cada equipamento, quando precisa devolver e quando esta atrasado.";

    private const string Commit = "eeee000011112222333344445555666677778888";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poseidon-contrafactual-{Guid.NewGuid():N}");

    public HistoricalDeliveryCounterfactualTests() => Directory.CreateDirectory(_root);

    /// <summary>
    /// O perfil efetivo resolvido do pedido original: um sistema que uma pessoa opera é WEB, e Web
    /// exige interface. Esta é a primeira trava — e ela não existia em 2026-08-02, quando o
    /// conceito de perfil efetivo não existia em documento, schema nem código.
    /// </summary>
    [Fact]
    public void OPedidoOriginalResolveComoProdutoWebQueExigeInterface()
    {
        var profile = EffectiveProfileResolver.Resolve(
            new EffectiveProfileInputs("emprestimos", PedidoOriginal, []));

        Assert.Equal(ProductModality.Web, profile.Modality);
        Assert.True(profile.Frontend.Required, "o perfil precisa exigir interface para este pedido.");
        Assert.True(profile.Api.Required);
        Assert.True(profile.Data.Required);

        var required = ProductDeliveryRequirements.For(profile);
        Assert.Contains(ProductEvidenceKind.FrontendPresent, required);
        Assert.Contains(ProductEvidenceKind.FrontendBuild, required);
        Assert.Contains(ProductEvidenceKind.E2EJourneyPassed, required);
        Assert.Contains(ProductEvidenceKind.FrontendBackendIntegration, required);
    }

    /// <summary>
    /// A ENTREGA HISTÓRICA no fechamento da fase 5, reproduzida: servidor Python em camadas, testes,
    /// runbook — e nenhuma interface. Era exatamente o estado que o portão de Desenvolvimento
    /// aprovou em 2026-08-04T13:18.
    ///
    /// Hoje ele reprova, e o motivo NOMEIA o que faltou. É a diferença entre "a fase fechou" e "a
    /// fase fechou porque ninguém perguntou pela metade que dá sentido ao produto".
    /// </summary>
    [Fact]
    public async Task AEntregaBackendOnlyQuePassouEm0408ReprovaComOsMecanismosDeHoje()
    {
        EscreverEntregaHistorica(comInterface: false);

        var (verdict, evidence, plan) = await AvaliarAsync();

        Assert.False(verdict.Satisfied, Diagnostico(verdict, evidence));

        // As lacunas que o portão da época não sabia enxergar.
        foreach (var kind in (ProductEvidenceKind[])
        [
            ProductEvidenceKind.FrontendPresent,
            ProductEvidenceKind.FrontendBuild,
            ProductEvidenceKind.FrontendBackendIntegration,
            ProductEvidenceKind.E2EJourneyPassed,
        ])
        {
            Assert.Contains(verdict.Findings, finding => finding.Kind == kind);
        }

        // E o plano não tem buraco de plataforma: cada requisito reprovado tem quem o verifique,
        // então a reprovação é sobre a ENTREGA, não sobre o Poseidon não saber verificar.
        Assert.Empty(plan.Unsupported);
    }

    /// <summary>
    /// A regressão nomeada do §Testes: <c>"sistema para empréstimo de equipamentos" → sem
    /// frontend</c> precisa permanecer IMPOSSÍVEL. Aqui a árvore ganha a interface que o run só
    /// produziu depois — e mesmo assim não passa, porque interface presente não é interface
    /// exercitada: a jornada continua sem prova.
    ///
    /// Este é o ponto que separa a Fase 6B do que existia antes. Presença é `Observed`; funcionar é
    /// `Verified`.
    /// </summary>
    [Fact]
    public async Task InterfacePresenteSemJornadaExercitadaContinuaSemFecharOPortao()
    {
        EscreverEntregaHistorica(comInterface: true);

        var (verdict, evidence, _) = await AvaliarAsync();

        Assert.False(verdict.Satisfied, Diagnostico(verdict, evidence));
        Assert.Contains(
            verdict.Findings, finding => finding.Kind == ProductEvidenceKind.E2EJourneyPassed);
    }

    /// <summary>
    /// A lacuna precisa chegar NOMEADA à fronteira da fase.
    ///
    /// No run de 2026-08-04 o portão sabia exatamente o que faltava e publicava um único
    /// `product:definition_of_done_failed` — verdadeiro e inútil: dele não sai trabalho corretivo, e
    /// foi por isso que a ausência de interface precisou de uma pessoa para virar card. O formato
    /// abaixo é o insumo mínimo para que a decisão possa ser tomada sem alguém traduzir.
    /// </summary>
    [Fact]
    public async Task CadaLacunaVirumCodigoProprioEmVezDeUmUnicoFalhouOpaco()
    {
        EscreverEntregaHistorica(comInterface: false);

        var (verdict, _, _) = await AvaliarAsync();
        var codigos = verdict.Findings
            .Select(finding =>
                $"product_gap:{finding.Kind.ToString().ToLowerInvariant()}:" +
                $"{finding.Gap.ToString().ToLowerInvariant()}")
            .ToArray();

        // `failed` e não `missing`: o inspetor de repositório PRODUZIU a evidência e ela diz que
        // não há interface. É uma constatação, não uma ausência — e a diferença importa para quem
        // vai decidir o que fazer a respeito.
        Assert.Contains("product_gap:frontendpresent:failed", codigos);
        Assert.Contains("product_gap:frontendbuild:failed", codigos);
        Assert.Contains("product_gap:e2ejourneypassed:failed", codigos);
        Assert.All(codigos, codigo => Assert.DoesNotContain("definition_of_done_failed", codigo, StringComparison.Ordinal));
    }

    private async Task<(ProductDeliveryVerdict Verdict, ProductDeliveryEvidence Evidence, ProductVerificationPlan Plan)>
        AvaliarAsync()
    {
        var profile = EffectiveProfileResolver.Resolve(
            new EffectiveProfileInputs("emprestimos", PedidoOriginal, []));
        var runner = new ProductVerificationRunner(
            ProductVerifierCatalog.CreateAll(new TrustedProcessRunner()));
        var plan = ProductVerificationPlan.From(
            profile, runner.NativeVerifiers, runner.ProjectControlledVerifiers);

        var records = await runner.RunAsync(
            new ProductVerificationContext(profile, _root, Commit, "emprestimos", "tentativa", "card"),
            plan,
            CancellationToken.None);

        var workspace = new FileSystemProductWorkspace(_root, Commit);
        var evidence = new ProductEvidenceCollectorPipeline().Collect(profile, workspace, records);
        return (ProductDeliveryGate.Evaluate(profile, evidence.Items, Commit, plan), evidence, plan);
    }

    private static string Diagnostico(
        ProductDeliveryVerdict verdict, ProductDeliveryEvidence evidence) =>
        verdict.Summary() + "\n" + string.Join(
            '\n', evidence.Items.Select(item => $"  {item.Kind} {item.Satisfied} {item.Level}"));

    /// <summary>
    /// A forma da entrega histórica: Python em camadas, testes, runbook, e a interface como HTML/CSS
    /// /JS servidos pelo próprio processo — que foi exatamente a decisão registrada no ADR de stack
    /// da interface. O conteúdo é reproduzido em miniatura; o que importa para o experimento é a
    /// FORMA, porque é sobre ela que os coletores e verificadores decidem.
    /// </summary>
    private void EscreverEntregaHistorica(bool comInterface)
    {
        foreach (var directory in (string[])
        [
            "src/emprestimos/api", "src/emprestimos/aplicacao", "src/emprestimos/dominio",
            "src/emprestimos/persistencia", "tests", "tools/backend",
        ])
        {
            Directory.CreateDirectory(
                Path.Combine(_root, directory.Replace('/', Path.DirectorySeparatorChar)));
        }

        File.WriteAllText(
            Path.Combine(_root, "src", "emprestimos", "dominio", "modelo.py"),
            "class Emprestimo:\n    def __init__(self, equipamento, membro, prazo):\n" +
            "        self.equipamento = equipamento\n        self.membro = membro\n" +
            "        self.prazo = prazo\n");
        File.WriteAllText(
            Path.Combine(_root, "src", "emprestimos", "api", "http.py"),
            "def aplicacao(environ, start_response):\n    return [b'{}']\n");
        File.WriteAllText(
            Path.Combine(_root, "src", "emprestimos", "persistencia", "esquema.py"),
            "ESQUEMA = 'CREATE TABLE emprestimos (id INTEGER PRIMARY KEY)'\n");
        File.WriteAllText(
            Path.Combine(_root, "tests", "test_dominio_atraso.py"),
            "def test_atraso():\n    assert True\n");
        File.WriteAllText(Path.Combine(_root, "tools", "backend", "test.sh"), "#!/bin/sh\nexit 0\n");
        File.WriteAllText(
            Path.Combine(_root, "README.md"),
            "# Controle de empréstimos\n\n## Como executar\n\n    python -m emprestimos.app\n");

        if (!comInterface)
        {
            return;
        }

        var interfaceDirectory = Path.Combine(_root, "src", "emprestimos", "interface");
        Directory.CreateDirectory(interfaceDirectory);
        File.WriteAllText(
            Path.Combine(interfaceDirectory, "index.html"),
            "<!doctype html><html><body><h1>Empréstimos</h1><div id=\"lista\"></div></body></html>");
        File.WriteAllText(
            Path.Combine(interfaceDirectory, "app.js"),
            "fetch('/api/emprestimos').then(r => r.json()).then(render);\n");
        File.WriteAllText(Path.Combine(interfaceDirectory, "estilo.css"), "body { font-family: sans-serif; }\n");
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
            // Sobra de árvore temporária não é falha de teste.
        }
    }
}
