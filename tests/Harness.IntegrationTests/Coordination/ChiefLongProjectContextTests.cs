using Harness.Host.Workers;
using Harness.IntegrationTests.Persistence;
using Harness.Modules.Governance.Context;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.IntegrationTests.Coordination;

/// <summary>
/// Fase 0A2 — regressão permanente de BR-006.
///
/// Um projeto longo perdia as decisões recentes por três motivos somados: a leitura do histórico era
/// CRESCENTE com limite (entregava o começo da conversa), a mensagem fundadora só sobrevivia por
/// acaso de ser a primeira da página, e as notas externalizadas eram write-only — a estratégia
/// gravava o que era crítico e ninguém lia de volta. O resultado era uma Bruna coerente e errada:
/// respondia com o contexto do primeiro dia.
/// </summary>
public sealed class ChiefLongProjectContextTests
{
    private const string Tenant = FoundationTransactionBehavior.TenantId;
    private const string Project = FoundationTransactionBehavior.ProjectId;

    /// <summary>O usuário local criado pelo provisionamento do projeto — autor das mensagens.</summary>
    private const string Author = "01ARZ3NDEKTSV4RRFFQ69G5FAY";

    [Fact]
    public async Task ALongConversationKeepsTheFoundingMandateAndTheRecentDecisions()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"ctx0a2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "context.db"), timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                FoundationTransactionBehavior.Command(), timeout.Token);
            var conversations = new SqliteConversationStore(dispatcher);
            var notes = new SqliteChiefContextNoteStore(dispatcher);
            var now = new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
            var conversationId = UlidValue.New(now).ToString();
            await conversations.CreateConversationAsync(
                new ConversationCreateCommand(
                    new ConversationRecord(
                        Tenant, conversationId, Project, "Projeto longo", "active",
                        Author, now, null, 1),
                    now),
                timeout.Token);

            // 260 mensagens: mais do que o limite de varredura (200). O mandato é a primeira; a
            // decisão que importa é a última.
            const int total = 260;
            var founding = "O mandato do projeto é publicar o catálogo com pagamento no cartão.";
            var lastDecision = "DECISÃO FINAL: o pagamento será por PIX, não por cartão.";
            for (var index = 0; index < total; index++)
            {
                var at = now.AddMinutes(index + 1);
                var content = index switch
                {
                    0 => founding,
                    total - 1 => lastDecision,
                    _ => $"Mensagem intermediária número {index} sobre detalhes de execução.",
                };
                var created = await conversations.CreateMessageAsync(
                    new MessageCreateCommand(
                        Tenant,
                        new MessageRecord(
                            Tenant, Project, UlidValue.New(at).ToString(), conversationId,
                            "user", Author, null, content, null, at),
                        at),
                    timeout.Token);
                Assert.Equal(MessageMutationStatus.Applied, created.Status);
            }

            // Uma nota externalizada de um turno anterior, e outra de OUTRO projeto.
            await notes.AppendAsync(
                new ChiefContextNoteAppendCommand(
                    Tenant, Project, conversationId, UlidValue.New(now).ToString(),
                    [
                        new ChiefContextNoteEntry(
                            UlidValue.New(now.AddSeconds(1)).ToString(),
                            UlidValue.New(now.AddSeconds(2)).ToString(),
                            "chief",
                            "Restrição registrada: o cliente exige nota fiscal em toda venda.",
                            24,
                            1),
                    ],
                    now),
                timeout.Token);

            var options = new ChiefContextStrategyOptions(
                true, new ContextStrategyBudget(4_000, 12, 4), 200);
            var composer = new ChiefContextComposer(
                conversations, new DefaultContextStrategy(), notes, new StubClock(now), options);
            var composition = await composer.ComposeAsync(
                Tenant, Project, conversationId, UlidValue.New(now).ToString(), timeout.Token);

            // O mandato fundador sobrevive — ele é o que a conversa inteira tenta cumprir.
            Assert.Contains(founding, composition.RenderedContext, StringComparison.Ordinal);
            // A decisão mais recente está presente. Antes, ela era a primeira coisa a sumir.
            Assert.Contains(lastDecision, composition.RenderedContext, StringComparison.Ordinal);
            // O meio antigo da conversa é descartado pelo orçamento — é o que a compactação existe
            // para fazer, e o que a leitura crescente fazia ao contrário.
            Assert.DoesNotContain(
                "Mensagem intermediária número 3 ", composition.RenderedContext, StringComparison.Ordinal);
            // A nota externalizada volta ao contexto COM proveniência.
            Assert.Contains(
                "Restrição registrada: o cliente exige nota fiscal",
                composition.RenderedContext,
                StringComparison.Ordinal);
            Assert.Contains("[nota durável · origem", composition.RenderedContext, StringComparison.Ordinal);
            Assert.True(composition.EstimatedTokens <= options.Budget.MaxTokens);

            // A fundadora aparece UMA vez, mesmo estando também entre as recentes de conversas curtas.
            var occurrences = composition.RenderedContext.Split(founding).Length - 1;
            Assert.Equal(1, occurrences);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RecentMessagesAreSelectedFromTheEndAndNotFromTheBeginning()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"ctx0a2-recent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "context.db"), timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                FoundationTransactionBehavior.Command(), timeout.Token);
            var conversations = new SqliteConversationStore(dispatcher);
            var now = new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
            var conversationId = UlidValue.New(now).ToString();
            await conversations.CreateConversationAsync(
                new ConversationCreateCommand(
                    new ConversationRecord(
                        Tenant, conversationId, Project, "Ordem", "active",
                        Author, now, null, 1),
                    now),
                timeout.Token);
            for (var index = 0; index < 50; index++)
            {
                var at = now.AddMinutes(index + 1);
                await conversations.CreateMessageAsync(
                    new MessageCreateCommand(
                        Tenant,
                        new MessageRecord(
                            Tenant, Project, UlidValue.New(at).ToString(), conversationId,
                            "user", Author, null, $"m{index}", null, at),
                        at),
                    timeout.Token);
            }

            var recent = await conversations.ListRecentMessagesAsync(
                Tenant, conversationId, 5, timeout.Token);

            // As ÚLTIMAS cinco, em ordem cronológica. A leitura antiga devolvia m0..m4.
            Assert.Equal(["m45", "m46", "m47", "m48", "m49"], recent.Select(m => m.Content));
            var first = await conversations.GetFirstMessageAsync(Tenant, conversationId, timeout.Token);
            Assert.Equal("m0", first!.Content);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// O limite das notas precisa cortar as MAIS ANTIGAS — o mesmo oldest-N de BR-006, que foi
    /// corrigido no histórico de mensagens e ficou para trás nas notas. Enquanto a leitura era
    /// `ORDER BY sequence,id LIMIT`, um projeto que passasse do teto recebia para sempre as
    /// primeiras notas e descartava em silêncio tudo o que a Bruna registrou depois.
    /// </summary>
    [Fact]
    public async Task NoteRetrievalKeepsTheNewestNotesAndDropsTheOldest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"ctx0a2-newest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "context.db"), timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                FoundationTransactionBehavior.Command(), timeout.Token);
            var notes = new SqliteChiefContextNoteStore(dispatcher);
            var now = new DateTimeOffset(2026, 8, 2, 8, 0, 0, TimeSpan.Zero);

            const int total = 30;
            for (var index = 0; index < total; index++)
            {
                await notes.AppendAsync(
                    new ChiefContextNoteAppendCommand(
                        Tenant, Project, null, null,
                        [
                            new ChiefContextNoteEntry(
                                UlidValue.New(now.AddSeconds(index * 2)).ToString(),
                                UlidValue.New(now.AddSeconds((index * 2) + 1)).ToString(),
                                "chief",
                                $"decisao-{index}",
                                4,
                                index)
                        ],
                        now.AddSeconds(index * 2)),
                    timeout.Token);
            }

            const int limit = 10;
            var recovered = await notes.ListAsync(Tenant, Project, null, limit, timeout.Token);

            Assert.Equal(limit, recovered.Count);
            // As dez ÚLTIMAS decisões, em ordem cronológica — não as dez primeiras.
            Assert.Equal("decisao-20", recovered[0].Content);
            Assert.Equal("decisao-29", recovered[^1].Content);
            Assert.DoesNotContain(recovered, note => note.Content == "decisao-0");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task NotesFromAnotherProjectNeverEnterTheContext()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"ctx0a2-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "context.db"), timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            var foundation = new SqliteFoundationTransactionStore(dispatcher);
            await foundation.ProvisionProjectAsync(
                FoundationTransactionBehavior.Command(), timeout.Token);
            var notes = new SqliteChiefContextNoteStore(dispatcher);
            var conversations = new SqliteConversationStore(dispatcher);
            var now = new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
            // Um segundo projeto DO MESMO tenant e da MESMA organização: é o vazamento que
            // importa provar impossível — nota de projeto vizinho entrando no contexto deste.
            var otherProject = "01ARZ3NDEKTSV4RRFFQ69G5FD1";
            await new SqliteProjectStore(dispatcher).CreateAsync(
                new Harness.Persistence.Abstractions.Projects.ProjectCreateCommand(
                    Tenant,
                    new Harness.Persistence.Abstractions.Projects.ProjectRecord(
                        Tenant, otherProject, "01ARZ3NDEKTSV4RRFFQ69G5FAW", "Outro", "OUTRO",
                        "Projeto vizinho", "active", "medium", null, "local", "main", [],
                        new Harness.Persistence.Abstractions.Projects.ProjectBrandRecord(
                            null, null, null, null),
                        [Author], 1, "01ARZ3NDEKTSV4RRFFQ69G5FD4", "manual", now, now, 0),
                    now),
                timeout.Token);
            var conversationId = UlidValue.New(now).ToString();
            await conversations.CreateConversationAsync(
                new ConversationCreateCommand(
                    new ConversationRecord(
                        Tenant, conversationId, Project, "Escopo", "active",
                        Author, now, null, 1),
                    now),
                timeout.Token);
            await conversations.CreateMessageAsync(
                new MessageCreateCommand(
                    Tenant,
                    new MessageRecord(
                        Tenant, Project, UlidValue.New(now.AddMinutes(1)).ToString(), conversationId,
                        "user", Author, null, "Mandato deste projeto.", null,
                        now.AddMinutes(1)),
                    now.AddMinutes(1)),
                timeout.Token);

            await notes.AppendAsync(
                new ChiefContextNoteAppendCommand(
                    Tenant, otherProject, null, null,
                    [
                        new ChiefContextNoteEntry(
                            UlidValue.New(now.AddSeconds(3)).ToString(),
                            UlidValue.New(now.AddSeconds(4)).ToString(),
                            "chief",
                            "SEGREDO DE OUTRO PROJETO que jamais pode vazar para este contexto.",
                            20,
                            1),
                    ],
                    now),
                timeout.Token);

            var composer = new ChiefContextComposer(
                conversations,
                new DefaultContextStrategy(),
                notes,
                new StubClock(now),
                new ChiefContextStrategyOptions(true, ContextStrategyBudget.Default, 200));
            var composition = await composer.ComposeAsync(
                Tenant, Project, conversationId, UlidValue.New(now).ToString(), timeout.Token);

            Assert.DoesNotContain(
                "SEGREDO DE OUTRO PROJETO", composition.RenderedContext, StringComparison.Ordinal);
            Assert.Contains("Mandato deste projeto.", composition.RenderedContext, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
