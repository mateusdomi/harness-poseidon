using Harness.Host.Product;

namespace Harness.IntegrationTests.Product;

/// <summary>
/// F-39 fechado no que dá para fechar antes do Golden Run: <b>o navegador abre nesta máquina?</b>
///
/// O verificador de jornada não podia ser exercitado no caminho feliz porque o ambiente de
/// verificação é offline por decisão de segurança, e baixar navegador durante a verificação de
/// código escrito por agente é exatamente o que não se faz. A saída não é relaxar o isolamento — é
/// separar as duas perguntas:
///
/// - <b>o runtime funciona?</b> se resolve AGORA, com recursos que o Poseidon já tem, numa página
///   servida por ele mesmo. É este arquivo;
/// - <b>a jornada da entrega passa?</b> só o Golden Run responde.
///
/// O smoke depende de provisionamento de MÁQUINA. Onde ele não existir, o teste não inventa
/// aprovação nem falha: ele registra o motivo e o preflight devolve `NOT_READY`, que é o
/// diagnóstico correto para uma instalação que ainda não pode rodar o Golden Run.
/// </summary>
[Collection(ProcessVerificationGroup.Name)]
public sealed class GoldenRunPreflightTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private static ProductVerificationRunner Verifiers() =>
        new(ProductVerifierCatalog.CreateAll(new TrustedProcessRunner()));

    [Fact]
    public async Task OPreflightInspecionaTudoQueOGoldenRunPrecisaEDizOQueFalta()
    {
        var report = await new GoldenRunPreflight(new TrustedProcessRunner())
            .InspectAsync(RepositoryRoot(), Verifiers(), CancellationToken.None);

        // As checagens que não dependem de provisionamento externo precisam estar prontas em
        // qualquer máquina que consiga rodar esta suíte — ela mesma usa dotnet, git e portas.
        foreach (var essential in (string[])["dotnet", "git", "loopback_ports", "workspace", "native_verifiers"])
        {
            var check = report.Checks.Single(item => item.Name == essential);
            Assert.Equal(PreflightStatus.Ready, check.Status);
            Assert.False(string.IsNullOrWhiteSpace(check.Detail), $"{essential} passou sem dizer o que encontrou.");
        }

        // O relatório nunca resume a "ok": quando algo falta, o motivo tem código estável.
        Assert.Equal(report.Ready, report.MissingReasons.Count == 0);
        Assert.Contains("golden_run_preflight", report.Summary(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Nenhum requisito crítico do Golden Run pode estar sem verificador nativo. É a mesma
    /// afirmação de <c>GoldenRunReadinessTests</c>, feita agora do ponto de vista da INSTALAÇÃO —
    /// o plano pode estar completo e a máquina não ter como executá-lo.
    /// </summary>
    [Fact]
    public async Task ORegistroDeVerificadoresNativosEstaCompletoNestaInstalacao()
    {
        var report = await new GoldenRunPreflight(new TrustedProcessRunner())
            .InspectAsync(RepositoryRoot(), Verifiers(), CancellationToken.None);

        var check = report.Checks.Single(item => item.Name == "native_verifiers");
        Assert.Equal(PreflightStatus.Ready, check.Status);
    }

    /// <summary>
    /// O SMOKE. Onde o runtime está provisionado, ele abre o navegador de verdade, navega numa
    /// página servida pelo próprio Poseidon, lê o conteúdo e fecha — sem rede e sem tocar em
    /// nenhuma worktree de agente.
    /// </summary>
    [Fact]
    public async Task ONavegadorAbreENavegaQuandoORuntimeEstaProvisionado()
    {
        var root = RepositoryRoot();
        var (runtime, moduleRoot) = GoldenRunPreflight.PlaywrightRuntime(root);
        var (browsers, browsersPath) = GoldenRunPreflight.Browsers();

        if (moduleRoot is null || browsersPath is null)
        {
            // Instalação sem runtime: o preflight PRECISA dizer isso, e é o que se afirma aqui.
            // Fabricar um sucesso seria mentir sobre a única coisa que este teste existe para saber.
            Assert.True(
                runtime.Status == PreflightStatus.Missing || browsers.Status == PreflightStatus.Missing,
                "sem runtime provisionado, o preflight tem de reportar ausência.");
            return;
        }

        var report = await new GoldenRunPreflight(new TrustedProcessRunner())
            .InspectAsync(root, Verifiers(), CancellationToken.None);

        var smoke = report.Checks.Single(item => item.Name == "browser_smoke");
        Assert.Equal(PreflightStatus.Ready, smoke.Status);
        Assert.Contains("navegador abriu", smoke.Detail, StringComparison.Ordinal);
    }
}
