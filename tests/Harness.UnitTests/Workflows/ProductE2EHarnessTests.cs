using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// O contrato do manifesto E2E — a peça que tira a prova de navegador do relato (não confiável)
/// do ator e a entrega à plataforma. Estes testes cobrem o parse; a orquestração (compose, boot,
/// playwright) vive no host e é exercitada contra produtos vivos.
/// </summary>
public sealed class ProductE2EHarnessTests
{
    private static string WriteManifest(string content)
    {
        var root = Path.Combine(Path.GetTempPath(), "e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".harness"));
        File.WriteAllText(Path.Combine(root, ProductE2EHarness.ManifestPath), content);
        return root;
    }

    [Fact]
    public void CarregaManifestoCompleto()
    {
        var root = WriteManifest(
            """
            {
              "composeFile": "infra/docker-compose.yml",
              "composeService": "oracle",
              "apiProject": "src/Indicadores.Api",
              "apiHealthPath": "/health",
              "apiUrl": "http://127.0.0.1:57381",
              "e2eDir": "tests/e2e",
              "e2eCommand": ["npx", "playwright", "test"],
              "env": { "E2E_API_URL": "http://127.0.0.1:57381" }
            }
            """);

        var harness = ProductE2EHarness.TryLoad(root);

        Assert.NotNull(harness);
        Assert.Equal("infra/docker-compose.yml", harness!.ComposeFile);
        Assert.Equal("tests/e2e", harness.E2eDir);
        Assert.Equal(["npx", "playwright", "test"], harness.E2eCommand);
        Assert.Equal("http://127.0.0.1:57381", harness.Env["E2E_API_URL"]);
    }

    [Fact]
    public void AusenciaDoManifestoNaoEErroEReprovacaoDoGate()
    {
        var root = Path.Combine(Path.GetTempPath(), "e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // Ausência devolve null: um produto com interface e sem manifesto E2E não pode se provar,
        // e isso é decisão do gate (reprovar), não exceção do parser.
        Assert.Null(ProductE2EHarness.TryLoad(root));
    }

    [Fact]
    public void ManifestoInvalidoOuIncompletoDevolveNull()
    {
        Assert.Null(ProductE2EHarness.TryLoad(WriteManifest("{ isso não é json")));
        // e2eCommand vazio: sem comando não há prova — inválido.
        Assert.Null(ProductE2EHarness.TryLoad(WriteManifest(
            """{ "e2eDir": "tests/e2e", "e2eCommand": [], "apiUrl": "x", "apiHealthPath": "/health", "env": {} }""")));
    }
}
