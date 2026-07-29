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

        var history = await _conversations.ListMessagesAsync(
            tenantId, conversationId, null, _options.HistoryScanLimit, cancellationToken);
        var items = MapToContextItems(history);
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

    // Mapeia o histórico durável da conversa para itens de contexto ordenados. A mensagem MAIS
    // ANTIGA (o mandato fundador) é fixada e marcada como crítica: é exatamente o tipo de fato que
    // deve ser externalizado como nota durável e sobreviver à compactação para sempre.
    private static ContextItem[] MapToContextItems(IReadOnlyList<MessageRecord> history)
    {
        var items = new ContextItem[history.Count];
        for (var index = 0; index < history.Count; index++)
        {
            var message = history[index];
            var founding = index == 0;
            items[index] = new ContextItem(
                message.Id,
                message.AuthorRole,
                ContextItemKind.Message,
                message.Content,
                message.TokenCount ?? GovernanceManifestSynchronizer.EstimateTokens(message.Content),
                index,
                Pinned: founding,
                Critical: founding);
        }

        return items;
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
