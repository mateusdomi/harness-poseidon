using System.Globalization;
using Harness.Host.RunTargets;

namespace Harness.IntegrationTests.RunTargets;

/// <summary>
/// F8/D8: o manifesto do projeto marca QUAL serviço entrega a tela ao cliente.
///
/// É a marcação que o modo Negócio usa para expor um endereço só — e ela existe para
/// não obrigar o dono a adivinhar, entre uma dezena de serviços, qual é o produto dele.
/// A regra é conservadora de propósito: marcar exige evidência no manifesto (framework de
/// interface declarado ou `index.html` servido). Sem evidência, nada é marcado, e a tela
/// diz que não sabe em vez de eleger um serviço qualquer.
/// </summary>
public sealed class RunTargetUserFacingDetectionTests
{
    [Fact]
    public async Task OnlyServicesWithInterfaceEvidenceAreMarkedUserFacing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"user-facing-{Guid.NewGuid():N}");
        var web = Path.Combine(root, "web");
        var api = Path.Combine(root, "api");
        var worker = Path.Combine(root, "worker");
        Directory.CreateDirectory(web);
        Directory.CreateDirectory(api);
        Directory.CreateDirectory(worker);

        try
        {
            // A tela do cliente: framework de interface declarado + index.html servido.
            await File.WriteAllTextAsync(
                Path.Combine(web, "package.json"),
                """
                {
                  "name": "loja-web",
                  "scripts": { "dev": "vite", "build": "vite build" },
                  "devDependencies": { "vite": "^5.0.0" }
                }
                """,
                timeout.Token);
            await File.WriteAllTextAsync(
                Path.Combine(web, "index.html"), "<!doctype html><title>Loja</title>", timeout.Token);

            // Serviço interno em Node: tem entrada executável e NENHUMA evidência de interface.
            await File.WriteAllTextAsync(
                Path.Combine(worker, "package.json"),
                """
                {
                  "name": "fila-worker",
                  "main": "server.js",
                  "dependencies": { "amqplib": "^0.10.0" }
                }
                """,
                timeout.Token);
            await File.WriteAllTextAsync(
                Path.Combine(worker, "server.js"), "// consome a fila", timeout.Token);

            // API .NET: sobe junto como dependência, nunca como a tela do cliente.
            await File.WriteAllTextAsync(
                Path.Combine(api, "Loja.Api.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>",
                timeout.Token);

            var detector = new RunTargetDetector();
            var targets = await detector.DetectAsync(root, timeout.Token);

            var front = Assert.Single(targets, target => target.Name.StartsWith("loja-web", StringComparison.Ordinal));
            Assert.True(front.UserFacing);
            // Porta/host explícitos: Vite e família ignoram a env PORT — sem o `-- --port`, a
            // URL registrada apontava para uma porta e o serviço subia em outra.
            Assert.Equal(
                ["npm", "run", "dev", "--", "--port", front.Port!.Value.ToString(CultureInfo.InvariantCulture), "--host", "127.0.0.1"],
                front.Arguments);

            var workerTarget = Assert.Single(
                targets, target => target.Name.StartsWith("fila-worker", StringComparison.Ordinal));
            Assert.False(workerTarget.UserFacing);

            var apiTarget = Assert.Single(
                targets, target => target.Name.EndsWith("(.NET)", StringComparison.Ordinal));
            Assert.False(apiTarget.UserFacing);

            // A regra do modo Negócio: existe UMA tela de cliente para abrir.
            Assert.Single(targets, target => target.UserFacing);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task WithoutInterfaceEvidenceNothingIsMarked()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"no-front-{Guid.NewGuid():N}");
        var service = Path.Combine(root, "service");
        Directory.CreateDirectory(service);

        try
        {
            // `dev` existe, mas o manifesto não declara interface nenhuma: não marca, e
            // também não inventa um alvo a partir do script de desenvolvimento.
            await File.WriteAllTextAsync(
                Path.Combine(service, "package.json"),
                """
                {
                  "name": "cobranca-service",
                  "scripts": { "dev": "nodemon src/index.js", "start": "node src/index.js" },
                  "dependencies": { "express": "^4.19.0" }
                }
                """,
                timeout.Token);

            var detector = new RunTargetDetector();
            var targets = await detector.DetectAsync(root, timeout.Token);

            var target = Assert.Single(targets);
            Assert.Equal("cobranca-service (npm start)", target.Name);
            Assert.False(target.UserFacing);
            Assert.DoesNotContain(targets, candidate => candidate.UserFacing);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
