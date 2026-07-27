using System.Globalization;

namespace Harness.Modules.Workflows.Application;

/// <summary>
/// A NATUREZA de uma obrigação de fase. Conjunto fechado. Existe para que o progresso pare de
/// confundir "o documento que descreve o trabalho" com "o trabalho": um briefing técnico e uma
/// migration são obrigações do mesmo plano, mas produzir o briefing não implementa nada.
/// </summary>
public enum PhaseObligationKind
{
    /// <summary>Artefato documental (entregável principal de fases de análise).</summary>
    Document,

    /// <summary>Trabalho de construção: código, migration, integração, infraestrutura.</summary>
    Implementation,

    /// <summary>Suíte ou cenário de teste que precisa EXECUTAR e passar.</summary>
    Test,

    /// <summary>Revisão independente (código ou documento) com veredito registrado.</summary>
    Review,

    /// <summary>Comprovação de trabalho: build, execução, screenshot, log.</summary>
    Evidence,

    /// <summary>Dado calculado a partir do que aconteceu (ex.: DORA).</summary>
    Metric,

    /// <summary>Escolha registrada entre alternativas.</summary>
    Decision,

    /// <summary>Atividade de release/homologação com resultado verificável.</summary>
    Operation,
}

/// <summary>
/// Estado de uma obrigação. Só <see cref="Accepted"/> conta como concluído; o resto aparece na
/// tela como situação operacional e NUNCA infla o percentual.
/// </summary>
public enum PhaseObligationState
{
    Pending,
    InProgress,
    InReview,
    Blocked,
    Accepted,
    Cancelled,
}

/// <summary>
/// Uma obrigação real da fase — a unidade de que o progresso é feito.
/// </summary>
public sealed record PhaseObligation(
    string ObligationKey,
    PhaseObligationKind Kind,
    string Description,
    bool Required,
    decimal Weight,
    PhaseObligationState State,
    string Source,
    string? CardId = null,
    string? ObjectiveKey = null,
    string? ArtifactRef = null);

/// <summary>
/// O retrato do progresso de uma fase: o percentual do que foi ACEITO e, separadamente, o que
/// ainda está em voo. A separação é o ponto — um card criado, atribuído, em execução ou em revisão
/// é trabalho em andamento, não trabalho entregue.
/// </summary>
public sealed record PhaseProgressSnapshot(
    decimal Percentage,
    int RequiredTotal,
    int RequiredAccepted,
    int InProgress,
    int InReview,
    int Blocked,
    int Pending,
    int OptionalTotal,
    int OptionalAccepted,
    bool TechnicallyComplete);

/// <summary>
/// Calcula o progresso de uma fase a partir das obrigações REAIS, não da existência de artefatos.
///
/// O defeito que este avaliador existe para corrigir: a esteira media a fase pelos documentos que
/// ela previa. Em fases de análise isso até coincide — o documento É o entregável —, mas em
/// Desenvolvimento, Testes, Homologação e Release produzir "briefing técnico", "code review
/// estruturado", "métricas DORA" e "dicionário ubíquo" fecharia a fase sem uma linha de código
/// implementada. Um documento conclui a própria obrigação e mais nada.
///
/// É puro e determinístico: as mesmas obrigações rendem sempre o mesmo percentual, para que o
/// número seja auditável e reproduzível.
/// </summary>
public static class PhaseProgressEvaluator
{
    public static PhaseProgressSnapshot Evaluate(IReadOnlyList<PhaseObligation> obligations)
    {
        ArgumentNullException.ThrowIfNull(obligations);

        // Obrigação cancelada sai da conta inteira — não do numerador só, o que permitiria fabricar
        // 100% cancelando o que falhou. A justificativa e o histórico ficam no registro durável;
        // aqui ela simplesmente não é mais uma obrigação do plano vigente.
        var live = obligations
            .Where(item => item.State != PhaseObligationState.Cancelled)
            .ToArray();

        var required = live.Where(item => item.Required).ToArray();
        var optional = live.Where(item => !item.Required).ToArray();

        // Peso zero ou negativo (obrigação sem peso declarado, ou dado corrompido) nunca pode
        // apagar a obrigação do denominador: isso faria uma fase com pendência real bater 100%
        // (a obrigação pendente some da conta) e, no outro extremo, faria uma fase inteiramente
        // aceita marcar 0% só porque nenhum peso foi atribuído. Toda obrigação obrigatória conta
        // ao menos como 1 unidade — é a mesma unidade que DefaultWeight já usa quando o plano não
        // declara peso.
        var totalWeight = required.Sum(item => EffectiveWeight(item.Weight));
        var acceptedWeight = required
            .Where(item => item.State == PhaseObligationState.Accepted)
            .Sum(item => EffectiveWeight(item.Weight));

        // Sem obrigação obrigatória não há o que medir: 0%, e a fase NUNCA está tecnicamente
        // pronta. Uma fase vazia que marcasse 100% aprovaria o nada.
        var percentage = totalWeight <= 0m
            ? 0m
            : Math.Round(acceptedWeight * 100m / totalWeight, 2, MidpointRounding.AwayFromZero);

        var requiredAccepted = required.Count(item => item.State == PhaseObligationState.Accepted);
        return new PhaseProgressSnapshot(
            percentage,
            required.Length,
            requiredAccepted,
            live.Count(item => item.State == PhaseObligationState.InProgress),
            live.Count(item => item.State == PhaseObligationState.InReview),
            live.Count(item => item.State == PhaseObligationState.Blocked),
            live.Count(item => item.State == PhaseObligationState.Pending),
            optional.Length,
            optional.Count(item => item.State == PhaseObligationState.Accepted),
            // Tecnicamente pronta = TODAS as obrigatórias aceitas e nenhuma bloqueada. É um estado
            // do TRABALHO; a aprovação humana é outro eixo e não altera este número — 100% pode
            // coexistir com "aguardando aprovação", e manter a fase em 99% por causa do humano
            // seria mentir sobre o que a equipe entregou.
            required.Length > 0 &&
                requiredAccepted == required.Length &&
                live.All(item => item.State != PhaseObligationState.Blocked));
    }

    /// <summary>
    /// Pesos NORMALIZADOS a partir das obrigações declaradas. Quando o plano não traz peso
    /// explícito (ou traz zero/negativo), todas as obrigatórias do mesmo plano valem igual — a
    /// alternativa, que era espalhar constantes pelo código, tornava o indicador impossível de
    /// auditar. Um peso declarado pelo plano prevalece.
    /// </summary>
    public static decimal DefaultWeight(int requiredCount) =>
        requiredCount <= 0 ? 0m : Math.Round(100m / requiredCount, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// O peso que uma obrigação REALMENTE vale na conta. Peso zero ou negativo não é "esta
    /// obrigação não conta" — é "nenhum peso foi declarado", e o piso é 1 unidade, a mesma unidade
    /// mínima usada por <see cref="DefaultWeight"/>.
    /// </summary>
    private static decimal EffectiveWeight(decimal weight) => weight > 0m ? weight : 1m;

    public static string ToStorage(PhaseObligationKind kind) => kind switch
    {
        PhaseObligationKind.Document => "document",
        PhaseObligationKind.Implementation => "implementation",
        PhaseObligationKind.Test => "test",
        PhaseObligationKind.Review => "review",
        PhaseObligationKind.Evidence => "evidence",
        PhaseObligationKind.Metric => "metric",
        PhaseObligationKind.Decision => "decision",
        _ => "operation",
    };

    public static PhaseObligationKind ParseKind(string value) =>
        value?.Trim().ToLower(CultureInfo.InvariantCulture) switch
        {
            "document" => PhaseObligationKind.Document,
            "implementation" => PhaseObligationKind.Implementation,
            "test" => PhaseObligationKind.Test,
            "review" => PhaseObligationKind.Review,
            "evidence" => PhaseObligationKind.Evidence,
            "metric" => PhaseObligationKind.Metric,
            "decision" => PhaseObligationKind.Decision,
            _ => PhaseObligationKind.Operation,
        };

    public static string ToStorage(PhaseObligationState state) => state switch
    {
        PhaseObligationState.Pending => "pending",
        PhaseObligationState.InProgress => "in_progress",
        PhaseObligationState.InReview => "in_review",
        PhaseObligationState.Blocked => "blocked",
        PhaseObligationState.Accepted => "accepted",
        _ => "cancelled",
    };

    public static PhaseObligationState ParseState(string value) =>
        value?.Trim().ToLower(CultureInfo.InvariantCulture) switch
        {
            "in_progress" => PhaseObligationState.InProgress,
            "in_review" => PhaseObligationState.InReview,
            "blocked" => PhaseObligationState.Blocked,
            "accepted" => PhaseObligationState.Accepted,
            "cancelled" => PhaseObligationState.Cancelled,
            _ => PhaseObligationState.Pending,
        };
}
