using Harness.Modules.Governance.Coordination;

namespace Harness.UnitTests.Governance;

/// <summary>
/// O estreitamento de escopo não pode tirar do card o lugar onde ele foi mandado escrever.
///
/// Ele existe para que dois cards do mesmo papel rodem em paralelo sem disputar arquivo — é
/// otimização. Quando a otimização corta a superfície do entregável, o card fica impedido de
/// cumprir a própria instrução, e o sintoma é o pior possível: o agente roda, gasta token e
/// entrega diff VAZIO, porque escrever era impossível. Não há erro, não há recusa visível, e o
/// revisor reprova por "não fez o trabalho".
///
/// Medido em 04/08/2026: um card cuja instrução dizia "UM arquivo ADR em docs/decisions/" recebeu
/// como único claim `tools/backend/**`. Sete tentativas, sete diffs vazios, sete reprovações
/// corretas — sobre um trabalho que o sistema tinha tornado impossível.
/// </summary>
public sealed class CardScopeDeclaredOutputTests
{
    private static readonly string[] BackendRole =
    [
        "src/**", "tests/**", "docs/architecture/**", "docs/contracts/**",
        "docs/decisions/**", "infra/**", "tools/backend/**",
    ];

    private static RepositorySurfaceMap Surfaces() =>
        RepositorySurfaceMap.ForSurfaces(
        [
            new RepositorySurface("ferramentas", ["build", "script", "verificação"], ["tools/backend/**"]),
            new RepositorySurface("registro-formal", ["registro-formal"], ["docs/decisions/**"]),
        ]);

    /// <summary>
    /// O caso real: a instrução nomeia `docs/decisions/` e o recorte casou só com ferramentas.
    /// Em vez de entregar um escopo que impede o trabalho, o planejador desiste do recorte.
    /// </summary>
    [Fact]
    public void RecorteQueNaoCobreOEntregavelDeclaradoEhAbandonado()
    {
        var plan = CardPathScopePlanner.Plan(
            BackendRole,
            Surfaces(),
            "Registrar decisão de build",
            "Produza UM arquivo em docs/decisions/ descrevendo o script de build.");

        Assert.False(plan.Narrowed);
        Assert.Equal("scope.declared_output_uncovered", plan.ReasonCode);
        Assert.Contains("docs/decisions/**", plan.Claims);
    }

    /// <summary>
    /// E o estreitamento continua acontecendo quando ele NÃO tira nada: é o caso comum, e
    /// desistir dele sempre custaria o paralelismo que ele existe para dar.
    /// </summary>
    [Fact]
    public void RecorteQueCobreOEntregavelContinuaValendo()
    {
        var plan = CardPathScopePlanner.Plan(
            BackendRole,
            Surfaces(),
            "Registro formal da escolha",
            "Produza UM registro-formal em docs/decisions/ com o trade-off.");

        Assert.True(plan.Narrowed);
        Assert.Equal("scope.narrowed", plan.ReasonCode);
        Assert.Contains("docs/decisions/**", plan.Claims);
    }

    /// <summary>
    /// A regra não vira porta de escalada: um caminho citado que o PAPEL não concede é ignorado.
    /// Só se protege o que o card já tinha direito de escrever.
    /// </summary>
    [Fact]
    public void CaminhoForaDoPapelNaoAmpliaNada()
    {
        var plan = CardPathScopePlanner.Plan(
            ["tools/backend/**"],
            Surfaces(),
            "Ajustar script de build",
            "Altere tools/backend/build.sh e também frontend/src/app.tsx.");

        Assert.DoesNotContain(plan.Claims, claim => claim.Contains("frontend", StringComparison.Ordinal));
    }

    /// <summary>
    /// Instrução sem caminho nomeado não muda nada — a regra só age quando há o que proteger.
    /// </summary>
    [Fact]
    public void InstrucaoSemCaminhoNomeadoNaoAtivaARegra()
    {
        var plan = CardPathScopePlanner.Plan(
            BackendRole,
            Surfaces(),
            "Ajustar o script de build",
            "Melhore a verificação de build conforme o padrão do projeto.");

        Assert.True(plan.Narrowed);
        Assert.Equal(["tools/backend/**"], plan.Claims);
    }
}
