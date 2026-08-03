using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Modules.Operations;
using Microsoft.Data.Sqlite;

namespace Harness.OperationSupervisor;

/// <summary>
/// Colhe as amostras do banco pessoal e emite `METRICS.json`.
///
/// Fica separado do cálculo de propósito: o cálculo é puro e testável, e a leitura é a parte
/// que depende do schema. O arquivo nasceu com todos os campos em `null` e um aviso de que não
/// havia coleta — é exatamente esse aviso que este coletor existe para retirar, com número
/// medido em vez de estimado.
/// </summary>
public static class MetricsCollector
{
    public static OperationMetricsReport Collect(string databasePath, string? projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
        connection.Open();

        var invocations = ReadInvocations(connection, projectId);
        var attempts = ReadAttempts(connection, projectId);
        var phases = ReadPhases(connection, projectId);
        return OperationMetricsCalculator.Calculate(invocations, attempts, phases);
    }

    private static List<InvocationSample> ReadInvocations(SqliteConnection connection, string? projectId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT attempt_id, work_task_id, outcome, estimated_cost_usd, duration_ms, " +
            "input_tokens, output_tokens FROM model_invocations " +
            "WHERE ($project IS NULL OR project_id = $project);";
        command.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);

        var samples = new List<InvocationSample>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            samples.Add(new InvocationSample(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDecimal(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6)));
        }

        return samples;
    }

    private static List<AttemptSample> ReadAttempts(SqliteConnection connection, string? projectId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, task_id, attempt_number, state, duration_ms FROM work_attempts " +
            "WHERE ($project IS NULL OR project_id = $project);";
        command.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);

        var samples = new List<AttemptSample>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            samples.Add(new AttemptSample(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }

        return samples;
    }

    /// <summary>
    /// Fase sem `completed_at` devolve tempo NULO, não zero: ela ainda está aberta, e tratá-la
    /// como instantânea distorceria a utilização de modelo para cima.
    /// </summary>
    private static List<PhaseSample> ReadPhases(SqliteConnection connection, string? projectId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT phase_order, activated_at, completed_at FROM workflow_phase_runs " +
            "WHERE ($project IS NULL OR project_id = $project) ORDER BY phase_order;";
        command.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);

        var samples = new List<PhaseSample>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            TimeSpan? wall = null;
            if (!reader.IsDBNull(1) && !reader.IsDBNull(2) &&
                DateTimeOffset.TryParse(
                    reader.GetString(1), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var started) &&
                DateTimeOffset.TryParse(
                    reader.GetString(2), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var finished))
            {
                wall = finished - started;
            }

            samples.Add(new PhaseSample(reader.GetInt32(0), wall));
        }

        return samples;
    }

    /// <summary>
    /// Escreve `METRICS.json` preservando a disciplina do arquivo: `null` continua sendo `null`,
    /// e a nota diz de onde os números vieram para que ninguém os confunda com estimativa.
    /// </summary>
    public static string Render(OperationMetricsReport report, string? projectId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(report);

        var json = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["instrumented"] = true,
            ["measuredAt"] = at.ToString("O", CultureInfo.InvariantCulture),
            ["scope"] = projectId ?? "todos os projetos",
            ["note"] =
                "Numeros MEDIDOS de model_invocations, work_attempts e workflow_phase_runs. " +
                "Campo nulo significa ausencia de base de calculo, nunca zero medido.",
            ["invocationCount"] = report.InvocationCount,
            ["totalInputTokens"] = report.TotalInputTokens,
            ["totalOutputTokens"] = report.TotalOutputTokens,
            ["totalCostUsd"] = decimal.Round(report.TotalCostUsd, 4),
            ["costPerAcceptedArtifact"] = Money(report.CostPerAcceptedArtifact),
            ["costPerDeliveredRequirement"] = Money(report.CostPerDeliveredRequirement),
            ["costOfRework"] = Money(report.CostOfRework),
            ["transientFailureWasteRate"] = Ratio(report.TransientFailureWasteRate),
            ["firstPassAcceptanceRate"] = Ratio(report.FirstPassAcceptanceRate),
            ["medianRunDurationSeconds"] = report.MedianRunDuration is { } median
                ? Math.Round(median.TotalSeconds, 1)
                : null,
            ["meanRunDurationSeconds"] = report.MeanRunDuration is { } mean
                ? Math.Round(mean.TotalSeconds, 1)
                : null,
            ["modelUtilization"] = Ratio(report.ModelUtilization),
            ["phaseWallTimeMinutes"] = new JsonArray(
                [.. report.PhaseWallTime.Select(span => span is { } value
                    ? JsonValue.Create(Math.Round(value.TotalMinutes, 1))
                    : null)]),
        };

        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonValue? Money(decimal? value) =>
        value is null ? null : JsonValue.Create(decimal.Round(value.Value, 4));

    private static JsonValue? Ratio(double? value) =>
        value is null ? null : JsonValue.Create(Math.Round(value.Value, 4));
}
