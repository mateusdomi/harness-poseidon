namespace Harness.Modules.Workflows.Product;

/// <summary>
/// Onde um requisito está na cadeia que vai do pedido até a prova.
///
/// A ordem é de força crescente, e cada degrau responde a uma pergunta diferente. O que o run de
/// 2026-08-04 não conseguia responder era justamente a diferença entre os três primeiros: havia
/// cards de interface, eles estavam no quadro, e ninguém sabia dizer que o requisito estava
/// descoberto — porque os cards tinham sido cancelados.
/// </summary>
public enum RequirementCoverageStatus
{
    /// <summary>
    /// Ninguém está cuidando disto. Ou nunca houve card, ou os que havia foram cancelados,
    /// arquivados ou substituídos sem que outro assumisse — que é o caso que custou a interface.
    /// </summary>
    Unplanned,

    /// <summary>Existe card vivo e o trabalho ainda não começou.</summary>
    Planned,

    /// <summary>Há trabalho em curso.</summary>
    InProgress,

    /// <summary>Um card obrigatório está bloqueado ou escalado: alguém precisa decidir.</summary>
    Blocked,

    /// <summary>Todo o trabalho fechou. O que NÃO significa que o produto funciona.</summary>
    Implemented,

    /// <summary>
    /// O trabalho fechou e o portão do produto aprovou a entrega. Só aqui o requisito está coberto
    /// de verdade: existe card, existe implementação e existe evidência verificada.
    /// </summary>
    Satisfied,

    /// <summary>O requisito deixou de valer, e outro o substituiu explicitamente.</summary>
    Superseded,
}

/// <summary>Um card visto do ponto de vista da cobertura: ele está vivo, e em que pé está.</summary>
public sealed record RequirementCard(string Id, string Title, string State, bool Archived)
{
    /// <summary>
    /// Card vivo é o que ainda pode entregar. Cancelado, arquivado e concluído são todos "não está
    /// mais cuidando disto" — e a diferença entre eles não importa para quem pergunta se o
    /// requisito tem dono.
    /// </summary>
    public bool IsAlive => !Archived &&
        !string.Equals(State, "cancelled", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(State, "done", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(State, "completed", StringComparison.OrdinalIgnoreCase);

    public bool IsDone => !Archived && (
        string.Equals(State, "done", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(State, "completed", StringComparison.OrdinalIgnoreCase));

    public bool IsBlocked =>
        string.Equals(State, "blocked", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(State, "escalated", StringComparison.OrdinalIgnoreCase);

    public bool HasStarted => !string.Equals(State, "ready", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Prova que liga um requisito/critério ao objetivo que o implementou e ao conjunto de evidência
/// que passou sobre o commit exato avaliado.
/// </summary>
public sealed record RequirementProof(
    string RequirementId,
    string ObjectiveCardId,
    string EvidenceSetId,
    string CommitSha,
    bool Passed,
    IReadOnlyList<string> AcceptanceCriteriaCovered,
    ProductEvidenceProvenance Provenance = ProductEvidenceProvenance.Verified);

/// <summary>O requisito e tudo o que se sabe sobre quem cuida dele.</summary>
public sealed record RequirementCoverage(
    string RequirementId,
    string Title,
    RequirementCoverageStatus Status,
    IReadOnlyList<string> AliveCardIds,
    string Reason)
{
    /// <summary>Coberto de verdade. Qualquer outra coisa é trabalho pendente ou buraco.</summary>
    public bool IsCovered => Status is RequirementCoverageStatus.Satisfied or
        RequirementCoverageStatus.Superseded;
}

/// <summary>
/// A cadeia que o run de 2026-08-04 não tinha:
///
/// <code>requisito → critério de aceite → card → implementação → teste → evidência</code>
///
/// Ela quebrava no primeiro elo. Havia requisito ("saber quem pegou cada equipamento"), havia
/// cards, e não havia nada que ligasse um ao outro de forma verificável — então quando os dois
/// cards de interface foram cancelados, o requisito de "uma pessoa consegue operar o sistema"
/// ficou sem dono e ninguém percebeu. A fase fechou.
///
/// Este modelo é <b>derivado</b> das fontes que já existem — demandas do usuário, cards do quadro e
/// o veredito do portão do produto. Não há tabela nova, não há grafo e não há segunda verdade: o
/// que ele faz é ler junto o que já estava gravado separado.
///
/// A regra que ele existe para impor está no status <see cref="RequirementCoverageStatus.Unplanned"/>:
/// <b>card cancelado não cobre requisito</b>. Enquanto isso não era dito em algum lugar, "o card
/// existiu" e "o requisito foi atendido" eram indistinguíveis.
/// </summary>
public static class RequirementCoverageAnalyzer
{
    /// <summary>Motivo que o portão publica quando um requisito obrigatório está descoberto.</summary>
    public const string UncoveredReasonCode = "product:requirement_uncovered";

    /// <summary>
    /// Cruza requisitos com cards. <paramref name="deliverySatisfied"/> é o veredito do portão do
    /// produto: sem ele, o máximo que um requisito alcança é <c>Implemented</c> — porque trabalho
    /// concluído não é produto funcionando, e essa distinção é a razão de o Product DoD existir.
    /// </summary>
    public static IReadOnlyList<RequirementCoverage> Analyze(
        IReadOnlyList<(string Id, string Title, bool Superseded)> requirements,
        IReadOnlyDictionary<string, IReadOnlyList<RequirementCard>> cardsByRequirement,
        bool deliverySatisfied)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(cardsByRequirement);

        var compatibilityProofs = deliverySatisfied
            ? requirements.SelectMany(requirement =>
                cardsByRequirement.TryGetValue(requirement.Id, out var cards)
                    ? cards.Where(card => card.IsDone).Select(card => new RequirementProof(
                        requirement.Id, card.Id, "legacy-global-product-verdict", "", true,
                        [requirement.Id]))
                    : [])
            : [];
        return Analyze(requirements, cardsByRequirement, compatibilityProofs.ToArray(), null);
    }

    /// <summary>
    /// Cruza requisitos, objetivos/cards e provas. Este é o caminho usado para prontidão de
    /// produto: requisito obrigatório só fica <see cref="RequirementCoverageStatus.Satisfied"/>
    /// quando existe evidência PASS válida, vinculada ao card objetivo e ao commit avaliado.
    /// </summary>
    public static IReadOnlyList<RequirementCoverage> Analyze(
        IReadOnlyList<(string Id, string Title, bool Superseded)> requirements,
        IReadOnlyDictionary<string, IReadOnlyList<RequirementCard>> cardsByRequirement,
        IReadOnlyList<RequirementProof> proofs,
        string? expectedCommitSha)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(cardsByRequirement);
        ArgumentNullException.ThrowIfNull(proofs);

        return [.. requirements.Select(requirement =>
        {
            if (requirement.Superseded)
            {
                return new RequirementCoverage(
                    requirement.Id, requirement.Title, RequirementCoverageStatus.Superseded, [],
                    "O requisito foi substituído e não é mais cobrado.");
            }

            var cards = cardsByRequirement.TryGetValue(requirement.Id, out var found) ? found : [];
            var alive = cards.Where(card => card.IsAlive).ToArray();
            var done = cards.Where(card => card.IsDone).ToArray();
            var blocked = alive.Where(card => card.IsBlocked).ToArray();
            var doneIds = done.Select(card => card.Id).ToHashSet(StringComparer.Ordinal);

            // NENHUM card vivo e NENHUM concluído: o requisito está descoberto. É exatamente o que
            // aconteceu com a interface — dois cards, ambos cancelados, requisito órfão e fase
            // fechando como se estivesse tudo certo.
            if (alive.Length == 0 && done.Length == 0)
            {
                return new RequirementCoverage(
                    requirement.Id, requirement.Title, RequirementCoverageStatus.Unplanned, [],
                    cards.Count == 0
                        ? "Nenhum card foi criado para este requisito."
                        : $"Os {cards.Count} card(s) deste requisito foram cancelados ou arquivados " +
                          "sem que outro assumisse o trabalho.");
            }

            if (blocked.Length > 0)
            {
                return new RequirementCoverage(
                    requirement.Id, requirement.Title, RequirementCoverageStatus.Blocked,
                    [.. alive.Select(card => card.Id)],
                    $"{blocked.Length} card(s) bloqueado(s) ou escalado(s): alguém precisa decidir.");
            }

            if (alive.Length > 0)
            {
                var started = alive.Any(card => card.HasStarted);
                return new RequirementCoverage(
                    requirement.Id, requirement.Title,
                    started ? RequirementCoverageStatus.InProgress : RequirementCoverageStatus.Planned,
                    [.. alive.Select(card => card.Id)],
                    $"{alive.Length} card(s) vivo(s){(started ? ", com trabalho em curso" : ", aguardando início")}.");
            }

            // Trabalho concluído. Só vira Satisfied quando o portão do produto também aprovou —
            // card fechado com entrega reprovada é trabalho feito e produto quebrado.
            var validProofs = proofs.Where(proof =>
                    string.Equals(proof.RequirementId, requirement.Id, StringComparison.Ordinal) &&
                    doneIds.Contains(proof.ObjectiveCardId) &&
                    proof.Passed &&
                    proof.Provenance >= ProductEvidenceProvenance.Verified &&
                    !string.IsNullOrWhiteSpace(proof.EvidenceSetId) &&
                    proof.AcceptanceCriteriaCovered.Count > 0 &&
                    (string.IsNullOrWhiteSpace(expectedCommitSha) ||
                        string.Equals(proof.CommitSha, expectedCommitSha, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            return validProofs.Length > 0
                ? new RequirementCoverage(
                    requirement.Id, requirement.Title, RequirementCoverageStatus.Satisfied, [],
                    $"{done.Length} card(s) concluído(s) e {validProofs.Length} evidência(s) PASS " +
                    "válida(s) cobrem o requisito no commit avaliado.")
                : new RequirementCoverage(
                    requirement.Id, requirement.Title, RequirementCoverageStatus.Implemented, [],
                    $"{done.Length} card(s) concluído(s), mas não há evidência PASS válida ligada " +
                    "ao requisito, ao objetivo e ao commit avaliado: trabalho feito não é produto funcionando.");
        })];
    }

    /// <summary>
    /// Os requisitos que impedem a fase de Desenvolvimento de fechar. Um requisito do usuário sem
    /// dono, ou bloqueado, é motivo suficiente — e o diagnóstico diz qual, para que ninguém precise
    /// ler o quadro inteiro para descobrir.
    /// </summary>
    public static IReadOnlyList<RequirementCoverage> Blocking(
        IReadOnlyList<RequirementCoverage> coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        return [.. coverage.Where(item => item.Status is
            RequirementCoverageStatus.Unplanned or RequirementCoverageStatus.Blocked)];
    }
}

public sealed record HumanAcceptanceReadinessVerdict(
    bool Ready,
    IReadOnlyList<RequirementCoverage> BlockingRequirements)
{
    public const string ReadyState = "READY_FOR_HUMAN_ACCEPTANCE";
}

/// <summary>
/// Gate final de prontidão humana. Diferente do gate de fase, aqui qualquer requisito obrigatório
/// que não esteja coberto por evidência válida impede o estado READY_FOR_HUMAN_ACCEPTANCE.
/// </summary>
public static class HumanAcceptanceReadinessGate
{
    public static HumanAcceptanceReadinessVerdict Evaluate(
        IReadOnlyList<RequirementCoverage> coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        var blocking = coverage.Where(item => !item.IsCovered).ToArray();
        return new HumanAcceptanceReadinessVerdict(blocking.Length == 0, blocking);
    }
}
