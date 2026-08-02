using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Modules.Operations;

/// <summary>
/// Um defeito da operação, como linha de `FINDINGS.jsonl`.
///
/// O formato é uma linha por defeito, de propósito: uma sessão nova lê o que falta sem
/// reconstruir narrativa, e um `nextAction` escrito enquanto o diagnóstico estava fresco
/// evita que o próximo executor re-derive a mesma investigação.
/// </summary>
public sealed record OperationFinding
{
    public string Id { get; init; } = string.Empty;

    /// <summary>critical | high | medium | low</summary>
    public string Severity { get; init; } = "medium";

    /// <summary>open | fixed | accepted | wont_fix</summary>
    public string Status { get; init; } = "open";

    /// <summary>
    /// Marcado à mão quando o defeito IMPEDE a operação de terminar. Severidade alta não
    /// implica bloqueio: um defeito grave já diagnosticado e agendado não precisa parar a
    /// prova, mas também não pode ser esquecido — por isso continua contando como trabalho
    /// executável.
    /// </summary>
    public bool Blocking { get; init; }

    public string? Area { get; init; }
    public string? Title { get; init; }
    public string? Cause { get; init; }
    public string? Fix { get; init; }
    public string? Evidence { get; init; }
    public string? Test { get; init; }
    public string? NextAction { get; init; }

    [JsonIgnore]
    public bool IsOpen => string.Equals(Status, "open", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsBlocking => IsOpen && Blocking;

    /// <summary>
    /// Trabalho executável é o que ainda tem ação conhecida. Um defeito aceito
    /// deliberadamente (`accepted`/`wont_fix`) não conta; um defeito aberto com
    /// `nextAction` conta, e é exatamente o que impede uma saída antecipada de virar DONE.
    /// </summary>
    [JsonIgnore]
    public bool IsExecutable => IsOpen && !string.IsNullOrWhiteSpace(NextAction);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Lê o arquivo linha a linha. Uma linha corrompida NÃO derruba a leitura das outras:
    /// perder a lista inteira de defeitos por causa de um caractere seria a pior forma de
    /// zerar o contador que impede a operação de fechar cedo.
    /// </summary>
    public static IReadOnlyList<OperationFinding> ParseLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var findings = new List<OperationFinding>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var finding = JsonSerializer.Deserialize<OperationFinding>(line, Options);
                if (finding is not null)
                {
                    findings.Add(finding);
                }
            }
            catch (JsonException)
            {
                // Linha ilegível vira um finding sintético: some do radar é pior que sujar.
                findings.Add(new OperationFinding
                {
                    Id = "PARSE-ERROR",
                    Severity = "high",
                    Status = "open",
                    Title = "Linha ilegível em FINDINGS.jsonl",
                    NextAction = "Reparar a linha corrompida",
                });
            }
        }

        return findings;
    }
}
