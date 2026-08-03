using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Application.Execution.External;

namespace Harness.UnitTests.Agents;

/// <summary>
/// O limite de SESSÃO da assinatura, medido ao vivo em 2026-08-03: catorze tentativas
/// seguidas, uma hora e quarenta minutos, zero token. A CLI escreveu o motivo no transcript
/// dela; o host lia outro arquivo, não achava nada, e classificava como transitório — o que
/// devolve o card à fila para bater na mesma parede.
/// </summary>
public sealed class ClaudeCodeSessionLimitDiagnosticTests
{
    private const string SessionLimit =
        "You've hit your session limit · resets 11:30am (America/Sao_Paulo)";

    /// <summary>Envelope de mensagem do transcript, como a CLI o escreve.</summary>
    private static string Envelope(string text) =>
        "{\"type\":\"assistant\",\"message\":{\"model\":\"<synthetic>\",\"role\":\"assistant\"," +
        "\"content\":[{\"type\":\"text\",\"text\":\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal) +
        "\"}]}}";

    [Fact]
    public void OTranscriptEntregaOTextoDeDentroDoEnvelope()
    {
        var linhas = ClaudeCodeExternalAgentExecutor.ExtractTranscriptText(
        [
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":" +
                "[{\"type\":\"tool_result\",\"content\":\"150 docs/x.md\"}]}}",
            Envelope(SessionLimit),
        ]);

        Assert.Contains(SessionLimit, Assert.Single(linhas), StringComparison.Ordinal);
    }

    [Fact]
    public void LinhaQueNaoEJsonNaoDerrubaAExtracao()
    {
        // O transcript pode terminar truncado numa queda; uma linha pela metade não pode
        // custar o diagnóstico inteiro.
        var linhas = ClaudeCodeExternalAgentExecutor.ExtractTranscriptText(
        [
            "{ isto não é json",
            Envelope(SessionLimit),
        ]);

        Assert.Single(linhas);
    }

    [Fact]
    public void TranscriptEProcuradoPeloIdentificadorDaSessao()
    {
        // A pasta é derivada do diretório de trabalho por uma regra da CLI que já mudou de
        // forma; a busca é pelo identificador, que é único.
        var raiz = Directory.CreateTempSubdirectory("poseidon-transcript-").FullName;
        try
        {
            var sessao = "adcdb21c-bd9f-433f-bbe9-c5958aaa9bfe";
            var pasta = Path.Combine(raiz, "projects", "-Users-alguem--harness-worktrees-01ABC");
            Directory.CreateDirectory(pasta);
            var esperado = Path.Combine(pasta, $"{sessao}.jsonl");
            File.WriteAllText(esperado, "{}");

            Assert.Equal(esperado, ClaudeCodeExternalAgentExecutor.FindTranscript(raiz, sessao));
            Assert.Null(ClaudeCodeExternalAgentExecutor.FindTranscript(raiz, "sessao-que-nao-existe"));
        }
        finally
        {
            Directory.Delete(raiz, true);
        }
    }

    [Fact]
    public void LimiteDeSessaoNoDiagnosticoDeixaDeSerTransitorio()
    {
        // ANTES: o código cru `executor.exit_code_1` casava o sinal `exit_code` e virava
        // `run.transient_failure` — cooldown curto, reeleição, mesma parede.
        var desfecho = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed, "executor.exit_code_1", SessionLimit);

        Assert.Equal(AgentRunOutcomeKind.QuotaExhausted, desfecho.Kind);
        Assert.Equal("run.quota_exhausted", desfecho.ReasonCode);
    }

    [Fact]
    public void OHorarioLocalDeclaradoViraAJanelaAteAProximaOcorrencia()
    {
        // 10:05 em São Paulo (13:05Z): faltam 1h25 para as 11:30 locais. Sem ler o horário
        // declarado, o padrão conservador de três horas manteria a conta fora bem depois de
        // ela já ter voltado.
        var agora = new DateTimeOffset(2026, 8, 3, 13, 5, 0, TimeSpan.Zero);

        var janela = AgentRunOutcomeClassifier.ResolveQuotaCooldown(SessionLimit, null, agora);

        Assert.Equal(TimeSpan.FromMinutes(85), janela);
    }

    [Fact]
    public void HorarioJaPassadoHojeApontaParaODeAmanha()
    {
        // 15:00 em São Paulo: as 11:30 de hoje já foram, então o reset é o de amanhã.
        var agora = new DateTimeOffset(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);

        var janela = AgentRunOutcomeClassifier.ResolveQuotaCooldown(SessionLimit, null, agora);

        Assert.Equal(TimeSpan.FromMinutes(20 * 60 + 30), janela);
    }

    [Fact]
    public void FusoDesconhecidoCaiNoConservadorEmVezDeInventarInstante()
    {
        var janela = AgentRunOutcomeClassifier.ResolveQuotaCooldown(
            "hit your session limit · resets 11:30am (Nenhum/Lugar)",
            null,
            new DateTimeOffset(2026, 8, 3, 13, 5, 0, TimeSpan.Zero));

        Assert.Equal(AgentRunOutcomeClassifier.DefaultQuotaCooldown, janela);
    }
}
