namespace Harness.Modules.Coordination.Application;

/// <summary>
/// Fatos puros de um card já coletados do board — sem IO. <see cref="CardType"/> é o tipo do card
/// ('feature','agent_task','human_gate','spike','decision'); <see cref="HasInstruction"/> indica se
/// existe ao menos uma versão de instrução; <see cref="IsBlocked"/> reflete board_state 'blocked' ou
/// um blocked_reason presente.
/// </summary>
/// <param name="StaleUpstream">
/// Onda 2.3 — nós predecessores (via depends_on/blocked_by/derives_from, transitivo) marcados
/// STALE na ProjectGraphProjection. Preenchido pelo Host SOMENTE com a flag
/// `graph.projection.enabled` ligada; nulo/vazio quando desligada — o avaliador continua puro e
/// o comportamento sem grafo é idêntico ao anterior.
/// </param>
/// <param name="IncompleteUpstream">
/// Predecessores (mesma travessia) cujo card de origem ainda não está concluído. Um card cujo
/// insumo não existe é despachado para falhar — foi o "review fora de ordem" do run de
/// empréstimos.
/// </param>
/// <param name="InstructionBody">
/// Corpo da versão de instrução mais recente, já buscado pelo chamador para outros fins (montar o
/// despacho) — sem IO extra aqui. Usado só para o Slice Readiness Gate (<see
/// cref="CardReadinessEvaluator.CardTooLarge"/>); nulo/vazio desliga o gate sem afetar o resto da
/// avaliação.
/// </param>
public sealed record CardReadinessFacts(
    string CardType,
    bool HasInstruction,
    bool IsBlocked,
    IReadOnlyList<string>? StaleUpstream = null,
    IReadOnlyList<string>? IncompleteUpstream = null,
    string? InstructionBody = null);

/// <summary>
/// Veredito de prontidão-para-despacho (Definition of Ready) de um card. Somente leitura.
/// <see cref="IsDispatchable"/> só é verdadeiro quando NENHUM bloqueador tipado se aplica.
/// </summary>
public sealed record CardReadinessSnapshot(bool IsDispatchable, IReadOnlyList<string> Blockers);

/// <summary>
/// Avaliador PURO e determinístico da Definition of Ready (DoR) de um card. Espelha a forma do
/// <c>ReadinessEvaluator</c> (função pura, códigos de bloqueador tipados, sem IO, sem autoridade de
/// domínio). Regra fail-safe do loop autônomo do Chefe: tipos que representam TRABALHO executável
/// por um profissional são auto-despacháveis — implementação, pesquisa, documentação, revisão e
/// operação — sempre com ao menos uma instrução e sem bloqueio. Contêineres ('feature') e decisões
/// que pertencem ao humano ou à Diretora ('human_gate', 'gate' e 'decision') nunca são executados
/// automaticamente. Um ADR é trabalho delegável de análise e registro; a decisão que ele subsidia
/// continua protegida por card 'decision'/'gate'.
/// </summary>
public static class CardReadinessEvaluator
{
    public const string DispatchableCardType = "agent_task";

    /// <summary>Tipos que representam trabalho delegável (legado + vocabulário do playbook).</summary>
    public static readonly IReadOnlySet<string> DispatchableCardTypes =
        new HashSet<string>(
            [
                "agent_task", "spike",
                "historia", "tarefa", "bug", "adr", "documento", "revisao", "council",
                "incidente", "chamado",
            ],
            StringComparer.Ordinal);

    public const string CardTypeNotDispatchable = "dor.card_type.not_dispatchable";
    public const string InstructionMissing = "dor.instruction.missing";
    public const string Blocked = "dor.blocked";

    /// <summary>Onda 2.3: predecessor STALE no grafo — despachar seria construir sobre premissa invalidada.</summary>
    public const string UpstreamStale = "dor.graph.upstream_stale";

    /// <summary>Onda 2.3: predecessor incompleto — o insumo do card ainda não existe.</summary>
    public const string UpstreamIncomplete = "dor.graph.upstream_incomplete";

    /// <summary>
    /// Card Slice Readiness Gate — o card RBAC de Indicadores TrensRJ
    /// (01KZAVEASRRB5GDDDPR2VSTC9B, INC-EVAL-004) provou que "refinar cards ao menor recorte
    /// seguro e verificável independentemente" (standard-workflow.md, Fase 4) é regra de texto
    /// sem contraparte executável: o card consumiu 4 execuções reais, estourou o orçamento de
    /// rodadas e só foi resolvido fatiando-o manualmente DEPOIS do gasto. Este bloqueador pega o
    /// sinal ANTES do primeiro despacho, não depois da quarta tentativa reprovada.
    /// </summary>
    public const string CardTooLarge = "dor.card_too_large";

    /// <summary>
    /// Teto de critérios de aceite declarados sob um cabeçalho "Critérios de aceite"/"Acceptance
    /// Criteria" antes do próximo cabeçalho <c>##</c>. Acima disto o card provavelmente cobre mais
    /// de um incremento verificável independentemente — o proxy mais direto de "fatia grande
    /// demais" que existe no texto sem exigir nenhuma coluna nova.
    /// </summary>
    public const int MaxAcceptanceCriteria = 8;

    /// <summary>
    /// Teto de caminhos de arquivo citados explicitamente no corpo (crase com "/"). Proxy de
    /// superfície de mudança estimada — um card que já nomeia mais de uma dúzia de arquivos
    /// tende a ser vários cards costurados num só.
    /// </summary>
    public const int MaxReferencedPaths = 12;

    /// <summary>
    /// Teto de tamanho bruto do corpo, em caracteres — último recurso quando nenhum dos dois
    /// sinais estruturados dispara, para pegar instruções monolíticas sem lista nem caminho
    /// algum. Generoso de propósito: só existe para o caso sem nenhuma estrutura.
    /// </summary>
    public const int MaxInstructionBodyLength = 12_000;

    public static CardReadinessSnapshot Evaluate(CardReadinessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var blockers = new List<string>();
        if (!DispatchableCardTypes.Contains(facts.CardType))
        {
            blockers.Add(CardTypeNotDispatchable);
        }

        if (!facts.HasInstruction)
        {
            blockers.Add(InstructionMissing);
        }

        if (facts.IsBlocked)
        {
            blockers.Add(Blocked);
        }

        // Fail-closed com razão ENUNCIÁVEL: o bloqueador carrega o nó exato que segura o card,
        // para que "não despachou" nunca mais seja um mistério de log.
        foreach (var node in facts.StaleUpstream ?? [])
        {
            blockers.Add($"{UpstreamStale}:{node}");
        }

        foreach (var node in facts.IncompleteUpstream ?? [])
        {
            blockers.Add($"{UpstreamIncomplete}:{node}");
        }

        var sizeBlocker = SizeBlockerFor(facts.InstructionBody);
        if (sizeBlocker is not null)
        {
            blockers.Add(sizeBlocker);
        }

        return new CardReadinessSnapshot(blockers.Count == 0, blockers);
    }

    /// <summary>
    /// Motivo enunciável de tamanho, ou <c>null</c> quando o corpo cabe nos três tetos. Corpo
    /// ausente/vazio nunca bloqueia — o gate de tamanho não substitui <see cref="InstructionMissing"/>.
    /// </summary>
    private static string? SizeBlockerFor(string? instructionBody)
    {
        if (string.IsNullOrWhiteSpace(instructionBody))
        {
            return null;
        }

        var acceptanceCriteria = CountAcceptanceCriteria(instructionBody);
        if (acceptanceCriteria > MaxAcceptanceCriteria)
        {
            return $"{CardTooLarge}:acceptance_criteria:{acceptanceCriteria}";
        }

        var referencedPaths = CountReferencedPaths(instructionBody);
        if (referencedPaths > MaxReferencedPaths)
        {
            return $"{CardTooLarge}:referenced_paths:{referencedPaths}";
        }

        if (instructionBody.Length > MaxInstructionBodyLength)
        {
            return $"{CardTooLarge}:body_length:{instructionBody.Length}";
        }

        return null;
    }

    /// <summary>
    /// Conta linhas de lista (<c>-</c>, <c>*</c> ou <c>1.</c>) sob um cabeçalho de critérios de
    /// aceite, até o próximo cabeçalho <c>##</c> ou o fim do texto — mesmo padrão de varredura de
    /// bloco de <c>ReplanAttemptPolicy.StripReplanBlocks</c>. Sem cabeçalho reconhecível, conta
    /// zero: o proxy não adivinha estrutura que o autor não declarou.
    /// </summary>
    private static int CountAcceptanceCriteria(string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var count = 0;
        var insideSection = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("##", StringComparison.Ordinal))
            {
                insideSection = trimmed.Contains("critério", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains("acceptance criteria", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!insideSection)
            {
                continue;
            }

            if (IsListItem(trimmed))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsListItem(string trimmedLine) =>
        trimmedLine.StartsWith("- ", StringComparison.Ordinal) ||
        trimmedLine.StartsWith("* ", StringComparison.Ordinal) ||
        (trimmedLine.Length > 2 && char.IsDigit(trimmedLine[0]) &&
            trimmedLine.TrimStart(['0', '1', '2', '3', '4', '5', '6', '7', '8', '9'])
                .StartsWith(". ", StringComparison.Ordinal));

    /// <summary>
    /// Conta ocorrências, entre crases, de um trecho com pelo menos uma barra — proxy de caminho
    /// de arquivo citado explicitamente no enunciado (ex.: <c>`src/Foo/Bar.cs`</c>).
    /// </summary>
    private static int CountReferencedPaths(string body)
    {
        var count = 0;
        var index = 0;
        while (true)
        {
            var open = body.IndexOf('`', index);
            if (open < 0)
            {
                break;
            }

            var close = body.IndexOf('`', open + 1);
            if (close < 0)
            {
                break;
            }

            var span = body[(open + 1)..close];
            if (span.Contains('/') && !span.Contains(' ') && span.Length is > 2 and < 200)
            {
                count++;
            }

            index = close + 1;
        }

        return count;
    }
}
