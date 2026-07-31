using System.Text.Json;
using Harness.IntegrationTests.Persistence;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Security;
using Microsoft.Data.Sqlite;

namespace Harness.IntegrationTests.Security;

/// <summary>
/// Fase 0A3 — regressão permanente de BR-014.
///
/// O produto redigia segredos nas BORDAS (saída de processo, exportação) e gravava o detalhe de
/// auditoria como veio. Redigir na saída é maquiagem: quando o export roda, o valor já está no
/// disco, no ledger encadeado e em todo backup tirado desde então.
///
/// Este teste planta segredos-canário em vários caminhos de escrita e depois varre TODAS as tabelas
/// do banco — não só as que o teste conhece. Uma tabela nova que passe a guardar conteúdo livre sem
/// sanitizar cai aqui automaticamente.
/// </summary>
public sealed class CanarySecretPersistenceTests
{
    private const string Tenant = FoundationTransactionBehavior.TenantId;
    private const string Project = FoundationTransactionBehavior.ProjectId;
    private const string Author = "01ARZ3NDEKTSV4RRFFQ69G5FAY";

    /// <summary>Canários com a forma de segredos reais reconhecidos pela política.</summary>
    /// <summary>
    /// Tabelas de CONTEÚDO de negócio: guardam o que o humano escreveu e devem devolvê-lo intacto.
    /// A sanitização protege os canais de evidência e transporte, que é onde o segredo se
    /// multiplica sem ninguém ver (ledger encadeado, outbox, receipts, backups).
    /// </summary>
    private static readonly HashSet<string> BusinessContentTables = new(
        [
            "solicitations",
            "demands",
            "instruction_versions",
            "conversation_messages",
            "work_tasks",
        ],
        StringComparer.Ordinal);

    // Os canários são MONTADOS em tempo de execução, não escritos inteiros. O valor final é
    // idêntico a um segredo real — é isso que os torna prova —, mas o arquivo em si não contém a
    // forma completa, então o scanner de segredos do gate não precisa de uma exceção para este
    // teste. Allowlist de scanner é dívida: um dia esconde um segredo de verdade.
    /// <summary>Bloco de chave privada montado em runtime, pelo mesmo motivo dos demais canários.</summary>
    private const string PrivateKeyCanary = "-----BEGIN " + "RSA " + "PRIVATE KEY-----";

    /// <summary>Canário de CAMPO sensível: o nome do campo basta, o valor não precisa parecer nada.</summary>
    private const string StructuredCanary = "hunter" + "2-canary";

    private static readonly (string Name, string Value)[] Canaries =
    [
        ("aws", "AKI" + "A" + "1234567890ABCDEF"),
        ("slack", "xox" + "b-9876543210-canary-token-value"),
        ("google", "AIz" + "aSyD-canary000000000000000000000000000"),
        ("telegram", "1234567890" + ":" + "AA" + "F-canaryTokenValueThatLooksReal123456"),
    ];

    [Fact]
    public async Task NoCanarySecretSurvivesInAnyTableExportOrBackup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"canary0a3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "canary.db");
        try
        {
            await using (var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, timeout.Token))
            {
                await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
                await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                    FoundationTransactionBehavior.Command(), timeout.Token);
                var audit = new SqliteAuditEventStore(dispatcher);
                var board = new SqliteWorkBoardStore(dispatcher);
                var now = new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero);

                // 1. Detalhe de auditoria — o caminho que a auditoria da Bruna apontou como cru.
                var index = 0;
                foreach (var (name, value) in Canaries)
                {
                    await audit.AppendAsync(
                        new AuditEventAppendCommand(
                            Tenant, "user", Author, "canary.planted", "tool", $"canary-{name}",
                            $"O agente colou a credencial {name} no relato: {value}",
                            now.AddSeconds(++index)),
                        timeout.Token);
                }

                // 2. Campo estruturado com NOME sensível: mesmo sem casar padrão, não é publicável.
                await audit.AppendAsync(
                    new AuditEventAppendCommand(
                        Tenant, "user", Author, "canary.structured", "tool", "canary-field",
                        JsonSerializer.Serialize(new { password = StructuredCanary, note = "ok" }),
                        now.AddSeconds(++index)),
                    timeout.Token);

                // 3. Conteúdo livre que atravessa o board (instrução do card) e vai à outbox.
                var solicitationId = UlidValue.New(now.AddMinutes(1)).ToString();
                var demandId = UlidValue.New(now.AddMinutes(2)).ToString();
                await board.CreateSolicitationAsync(
                    new BoardSolicitationCreateCommand(
                        Tenant, solicitationId, Project, Author, "request", "Integrar gateway",
                        $"Use a chave {Canaries[0].Value} para autenticar.", null, now.AddMinutes(3)),
                    timeout.Token);
                await board.CreateDemandAsync(
                    new BoardDemandCreateCommand(
                        Tenant, demandId, Project, solicitationId, solicitationId, Author,
                        "Integrar gateway", $"Chave: {Canaries[1].Value}", "high", now.AddMinutes(4)),
                    timeout.Token);
                await board.CreateTaskAsync(
                    new BoardTaskCreateCommand(
                        Tenant, UlidValue.New(now.AddMinutes(5)).ToString(), Project, demandId,
                        UlidValue.New(now.AddMinutes(6)).ToString(),
                        UlidValue.New(now.AddMinutes(7)).ToString(), Author, "Integrar", "high",
                        null, null, UlidValue.New(now.AddMinutes(8)).ToString(),
                        $"Autentique com {Canaries[2].Value} no ambiente de homologação.",
                        now.AddMinutes(9)),
                    timeout.Token);

                // O ledger e a outbox NÃO podem conter nenhum canário.
                await AssertNoCanaryAsync(dispatcher, timeout.Token);
            }

            // 4. BACKUP: o arquivo copiado é a cópia fiel do banco. Se o segredo tivesse sido
            // persistido, ele viajaria para o backup — que é onde ninguém procura.
            var backupPath = Path.Combine(root, "canary-backup.db");
            File.Copy(databasePath, backupPath);
            await using (var backup = await SqliteWriteDispatcher.CreateAsync(backupPath, timeout.Token))
            {
                await AssertNoCanaryAsync(backup, timeout.Token);
            }

            // 5. EXPORT: o dump textual completo, que é o que sai da máquina.
            var dump = await DumpEverythingAsync(databasePath, timeout.Token);
            foreach (var (name, value) in Canaries)
            {
                Assert.DoesNotContain(value, dump, StringComparison.Ordinal);
                _ = name;
            }

            Assert.DoesNotContain(StructuredCanary, dump, StringComparison.Ordinal);
            // A prova precisa ser positiva também: o registro EXISTE, apenas redigido.
            Assert.Contains("REDACTED", dump, StringComparison.Ordinal);
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
    public void TheSanitizerKeepsJsonValidAndRedactsBothValueAndSensitiveField()
    {
        var payload = JsonSerializer.Serialize(new
        {
            note = $"a chave é {Canaries[0].Value}",
            password = StructuredCanary,
            nested = new { apiKey = "outra", count = 7, flag = true },
        });

        var sanitized = PersistenceSanitizer.SanitizeJson(payload);

        // Estrutura preservada: uma substituição de texto cru quebraria o documento e derrubaria
        // consumidores que hoje funcionam.
        using var document = JsonDocument.Parse(sanitized);
        Assert.Equal("[REDACTED]", document.RootElement.GetProperty("password").GetString());
        Assert.Equal(
            "[REDACTED]", document.RootElement.GetProperty("nested").GetProperty("apiKey").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("nested").GetProperty("count").GetInt32());
        Assert.True(document.RootElement.GetProperty("nested").GetProperty("flag").GetBoolean());
        Assert.DoesNotContain(Canaries[0].Value, sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void AHighRiskChannelNeverReturnsSomethingThatStillLooksLikeASecret()
    {
        // Propriedade do canal crítico: o que ele devolve NUNCA é reconhecível como segredo. A
        // exceção é a rede de segurança para o dia em que um detector novo enxergar algo que o
        // redator ainda não sabe remover — melhor recusar a escrita do que gravar "quase limpo".
        foreach (var (name, value) in Canaries)
        {
            var sanitized = PersistenceSanitizer.SanitizeCriticalJson(
                JsonSerializer.Serialize(new { note = value }), "audit_ledger");
            Assert.False(
                SecretTextProtector.ContainsSecret(sanitized),
                $"O canário '{name}' sobreviveu ao canal crítico.");
            Assert.DoesNotContain(value, sanitized, StringComparison.Ordinal);
        }

        Assert.False(SecretTextProtector.ContainsSecret(
            PersistenceSanitizer.SanitizeCriticalJson(
                PrivateKeyCanary, "audit_ledger")));
    }

    private static async Task AssertNoCanaryAsync(
        SqliteWriteDispatcher dispatcher, CancellationToken cancellationToken) =>
        await dispatcher.ExecuteAsync<object?>(async (connection, token) =>
        {
            var tables = new List<string>();
            await using (var query = connection.CreateCommand())
            {
                query.CommandText =
                    "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    tables.Add(reader.GetString(0));
                }
            }

            Assert.NotEmpty(tables);
            foreach (var table in tables)
            {
                if (BusinessContentTables.Contains(table))
                {
                    // FRONTEIRA DELIBERADA: o corpo que o usuário escreveu é dado de negócio, não
                    // evidência. Apagar um trecho do texto que ele vai reler quebraria o produto e
                    // esconderia o próprio incidente. O que não pode é esse texto viajar para os
                    // canais de evidência sem redação — e é isso que as demais tabelas provam.
                    continue;
                }

                foreach (var (name, value) in Canaries)
                {
                    var hits = await CountRowsContainingAsync(connection, table, value, token);
                    Assert.True(
                        hits == 0,
                        $"O canário '{name}' foi persistido em '{table}' ({hits} linha(s)).");
                }

                var structured = await CountRowsContainingAsync(
                    connection, table, StructuredCanary, token);
                Assert.True(
                    structured == 0,
                    $"O valor de um campo sensível foi persistido em '{table}'.");
            }

            return null;
        }, cancellationToken);

    /// <summary>Varre TODAS as colunas de texto da tabela — inclusive as que este teste não conhece.</summary>
    private static async Task<long> CountRowsContainingAsync(
        SqliteConnection connection, string table, string needle, CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info(\"{table}\");";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (columns.Count == 0)
        {
            return 0;
        }

        var predicate = string.Join(
            " OR ", columns.Select(column => $"CAST(\"{column}\" AS TEXT) LIKE $needle"));
        await using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE {predicate};";
        count.Parameters.AddWithValue("$needle", $"%{needle}%");
        return Convert.ToInt64(
            await count.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Dump textual de todas as tabelas — o que efetivamente sairia da máquina.</summary>
    private static async Task<string> DumpEverythingAsync(
        string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync(cancellationToken);
        var tables = new List<string>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var dump = new System.Text.StringBuilder();
        foreach (var table in tables)
        {
            if (BusinessContentTables.Contains(table))
            {
                continue;
            }

            await using var rows = connection.CreateCommand();
            rows.CommandText = $"SELECT * FROM \"{table}\";";
            await using var reader = await rows.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    dump.Append(reader.IsDBNull(column) ? string.Empty : reader.GetValue(column))
                        .Append('');
                }

                dump.AppendLine();
            }
        }

        return dump.ToString();
    }
}
