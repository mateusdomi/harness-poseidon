using System.Text;
using Harness.Modules.Governance.Context;
using Harness.Modules.Governance.Documentation;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Workers;

/// <summary>
/// Resultado da composição da janela de contexto do Chief: o texto renderizado (limitado), o total
/// estimado de tokens, se houve compactação, quantas notas NOVAS foram persistidas e quantos itens
/// a janela final tem.
/// </summary>
public sealed record ChiefContextComposition(
    string RenderedContext,
    int EstimatedTokens,
    bool Compacted,
    int PersistedNoteCount,
    int ItemCount);

public sealed record ChiefProjectBrandContext(
    string? LogoUrl,
    string? PrimaryColor,
    string? SecondaryColor,
    string? Typography);

public sealed record ChiefProjectContext(
    string ProjectId,
    string Title,
    string Objective,
    string Criticality,
    string TargetDeadline,
    ChiefProjectBrandContext Brand,
    IReadOnlyList<string> Technologies,
    string? WorkflowTemplateId,
    string? WorkflowName);

/// <summary>
/// Seam de integração da <see cref="IContextStrategy"/> no ponto onde o Chief monta o histórico que
/// envia ao modelo. Lê o histórico durável da conversa, aplica a estratégia (pura) para manter a
/// janela limitada, persiste as notas externalizadas (a memória vive no store) e devolve o contexto
/// renderizado. Componente fino e testável isoladamente da execução do agente.
/// </summary>
public sealed class ChiefContextComposer(
    IConversationStore conversations,
    IContextStrategy strategy,
    IChiefContextNoteStore notes,
    IClock clock,
    ChiefContextStrategyOptions options,
    IProjectStore? projects = null,
    IWorkflowCatalogStore? workflows = null)
{
    private readonly IConversationStore _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
    private readonly IContextStrategy _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
    private readonly IChiefContextNoteStore _notes = notes ?? throw new ArgumentNullException(nameof(notes));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ChiefContextStrategyOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IProjectStore? _projects = projects;
    private readonly IWorkflowCatalogStore? _workflows = workflows;

    /// <summary>
    /// Conjunto mínimo e tipado dos campos de projeto que a Bruna precisa para
    /// abrir o plano. Não inclui caminho local, branch, provedor ou outros
    /// detalhes operacionais que não pertencem ao intake de negócio.
    /// </summary>
    public async Task<ChiefProjectContext?> ComposeProjectAsync(
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        if (_projects is null || _workflows is null)
        {
            throw new InvalidOperationException(
                "Project and workflow stores are required to compose the Chief project context.");
        }

        var project = await _projects.GetAsync(tenantId, projectId, cancellationToken);
        if (project is null)
        {
            return null;
        }

        var binding = (await _workflows.ListBindingsAsync(
            tenantId,
            projectId,
            null,
            1,
            cancellationToken)).SingleOrDefault();
        var template = binding is null
            ? null
            : await _workflows.GetTemplateAsync(
                tenantId,
                binding.TemplateId,
                cancellationToken);

        return new ChiefProjectContext(
            project.Id,
            project.Name,
            project.Description,
            project.Criticality,
            project.TargetDeadline?.ToUniversalTime().ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture) ?? "sem prazo definido",
            new ChiefProjectBrandContext(
                project.Brand.LogoUrl,
                project.Brand.PrimaryColor,
                project.Brand.SecondaryColor,
                project.Brand.Typography),
            project.Technologies,
            binding?.TemplateId,
            template?.Name);
    }

    public async Task<ChiefContextComposition> ComposeAsync(
        string tenantId, string projectId, string conversationId, string turnId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        // Fase 0A2 (BR-006): as ÚLTIMAS N mensagens, não as primeiras. A leitura anterior era
        // crescente com LIMIT, então em um projeto longo a Bruna recebia o começo da conversa e
        // ignorava tudo o que tinha sido decidido depois — o pior tipo de erro, porque a resposta
        // sai coerente e errada.
        var recent = await _conversations.ListRecentMessagesAsync(
            tenantId, conversationId, _options.HistoryScanLimit, cancellationToken);

        // O mandato FUNDADOR não pode depender de caber na janela recente: ele é o que a conversa
        // inteira está tentando cumprir. Vem separado e é fixado — sem duplicar quando a conversa
        // ainda é curta e ele já está entre as recentes.
        var founding = await _conversations.GetFirstMessageAsync(
            tenantId, conversationId, cancellationToken);

        // Notas externalizadas eram WRITE-ONLY: a estratégia gravava fatos críticos que ninguém
        // jamais lia de volta. Reinjetá-las é o que torna a compactação uma memória, e não uma
        // perda. O store é keyed por (tenant, projeto), então nada de outro projeto entra aqui.
        var notes = await _notes.ListAsync(
            tenantId, projectId, conversationId, _options.NoteRetrievalLimit, cancellationToken);

        var items = MapToContextItems(recent, founding, notes);
        var result = _strategy.Apply(items, _options.Budget);

        var persisted = 0;
        if (result.Notes.Count > 0)
        {
            var now = _clock.UtcNow;
            var entries = result.Notes
                .Select((note, index) => new ChiefContextNoteEntry(
                    UlidValue.New(now.AddTicks(index)).ToString(),
                    note.SourceItemId,
                    note.Role,
                    note.Content,
                    note.EstimatedTokens,
                    note.Sequence))
                .ToArray();
            persisted = await _notes.AppendAsync(
                new ChiefContextNoteAppendCommand(tenantId, projectId, conversationId, turnId, entries, now),
                cancellationToken);
        }

        return new ChiefContextComposition(
            Render(result.Context), result.EstimatedTokens, result.Compacted, persisted, result.Context.Count);
    }

    /// <summary>
    /// Monta a janela: o mandato fundador (fixado e crítico), as notas já externalizadas e as
    /// mensagens recentes, nesta ordem e sem repetir ninguém. A sequência é crescente — maior é
    /// mais recente —, que é como a estratégia decide o que preservar verbatim.
    /// </summary>
    private static ContextItem[] MapToContextItems(
        IReadOnlyList<MessageRecord> recent,
        MessageRecord? founding,
        IReadOnlyList<ChiefContextNoteRecord> notes)
    {
        var items = new List<ContextItem>(recent.Count + notes.Count + 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sequence = 0L;

        if (founding is not null)
        {
            seen.Add(founding.Id);
            items.Add(new ContextItem(
                founding.Id,
                founding.AuthorRole,
                ContextItemKind.Message,
                founding.Content,
                founding.TokenCount ?? GovernanceManifestSynchronizer.EstimateTokens(founding.Content),
                sequence++,
                Pinned: true,
                Critical: true));
        }

        foreach (var note in notes)
        {
            // A nota entra com PROVENANCE: quem lê o contexto precisa saber que aquilo é um fato
            // externalizado de um turno anterior, e de qual item ele veio — sem isso, memória
            // recuperada é indistinguível de alucinação.
            if (!seen.Add(note.NoteId))
            {
                continue;
            }

            items.Add(new ContextItem(
                note.NoteId,
                note.Role,
                ContextItemKind.Note,
                $"[nota durável · origem {note.SourceItemId} · registrada em " +
                $"{note.CreatedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC]\n{note.Content}",
                note.TokenEstimate,
                sequence++,
                Pinned: true,
                Critical: false,
                Noted: true));
        }

        foreach (var message in recent)
        {
            // A fundadora pode estar entre as recentes quando a conversa ainda é curta: entra uma
            // vez só, e como fundadora.
            if (!seen.Add(message.Id))
            {
                continue;
            }

            items.Add(new ContextItem(
                message.Id,
                message.AuthorRole,
                ContextItemKind.Message,
                message.Content,
                message.TokenCount ?? GovernanceManifestSynchronizer.EstimateTokens(message.Content),
                sequence++));
        }

        return [.. items];
    }

    private static string Render(IReadOnlyList<ContextItem> items)
    {
        if (items.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in items)
        {
            builder.Append("## ").Append(item.Role).Append(" — ").Append(item.Kind).Append(" — ").AppendLine(item.Id)
                .AppendLine(item.Content.Trim()).AppendLine();
        }

        return builder.ToString().TrimEnd() + "\n";
    }
}
