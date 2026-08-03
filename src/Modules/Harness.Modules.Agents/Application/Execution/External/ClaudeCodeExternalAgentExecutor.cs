using System.Text.Json;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;

namespace Harness.Modules.Agents.Application.Execution.External;

/// <summary>
/// Adapter real do Claude Code (CA-4). É o executor do Chief.
///
/// Todas as flags foram OBSERVADAS na CLI instalada (2.1.216) e o formato de saída foi
/// observado numa execução real: `-p --output-format stream-json --verbose` emite uma linha
/// JSON por evento, com `system/init` carregando `session_id`, `assistant` carregando o
/// texto, `rate_limit_event` carregando o estado de cota e `result` carregando a mensagem
/// final, o custo e o consumo.
///
/// O mesmo adapter serve o GLM, que é o Claude Code apontado a um endpoint compatível por
/// variáveis de ambiente e por isso exige `CLAUDE_CONFIG_DIR` próprio.
/// </summary>
public sealed class ClaudeCodeExternalAgentExecutor(
    ExecutorProfile profile, AccountProfileProvisioner profiles, ExecutorProbe? probe = null)
    : ProcessExternalAgentExecutor(profile, profiles, probe)
{
    /// <summary>Ferramentas do papel de CRITIC: leitura apenas, nenhuma escrita.</summary>
    public static IReadOnlyList<string> ReadOnlyTools { get; } = ["Read", "Grep", "Glob"];

    public static ClaudeCodeExternalAgentExecutor ForClaudeCode(
        AccountProfileProvisioner profiles, ExecutorProbe? probe = null) =>
        new(ExecutorCatalog.Find(ExecutorCatalog.ClaudeCode)!, profiles, probe);

    public static ClaudeCodeExternalAgentExecutor ForGlm(
        AccountProfileProvisioner profiles, ExecutorProbe? probe = null) =>
        new(ExecutorCatalog.Find(ExecutorCatalog.Glm)!, profiles, probe);

    public override IReadOnlyList<string> BuildArguments(
        ExternalAgentRunRequest request, ExternalAgentRunContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = new List<string>
        {
            "-p", "--output-format", "stream-json", "--verbose",
        };

        if (request.ResumeSessionId is { Length: > 0 } sessionId)
        {
            arguments.AddRange(["--resume", sessionId]);
        }

        if (request.Model is { Length: > 0 } model)
        {
            arguments.AddRange(["--model", model]);
        }

        if (request.Effort is { Length: > 0 } effort)
        {
            arguments.AddRange(["--effort", effort]);
        }

        if (request.Access == ExternalAgentAccess.Workspace)
        {
            arguments.AddRange(["--permission-mode", "acceptEdits"]);
        }
        else
        {
            // Critic: sem Edit, Write ou Bash na lista de ferramentas, escrever é impossível
            // — a restrição não depende de o modelo "resolver não escrever".
            arguments.AddRange(["--tools", string.Join(',', ReadOnlyTools)]);
            arguments.AddRange(["--permission-mode", "dontAsk"]);
        }

        foreach (var directory in request.AdditionalDirectories)
        {
            arguments.AddRange(["--add-dir", directory]);
        }

        return arguments;
    }

    private protected override IExternalAgentOutputParser CreateParser(
        ExternalAgentRunRequest request, ExternalAgentRunContext context) =>
        new ClaudeStreamJsonParser();

    /// <summary>
    /// Parser do `stream-json` do Claude Code. Os nomes de campo abaixo foram observados na
    /// saída real da CLI instalada, não inferidos.
    /// </summary>
    internal sealed class ClaudeStreamJsonParser : IExternalAgentOutputParser
    {
        public string? SessionId { get; private set; }

        public string? FinalMessage { get; private set; }

        public ExternalAgentUsage? Usage { get; private set; }

        public string? FailureCode { get; private set; }

        /// <summary>
        /// A falta de credencial também sai no stderr (modo não-stream e algumas versões da
        /// CLI). Mesmo motivo do ramo de stdout: sem o código estruturado, a conta caía como
        /// transitória e voltava à eleição a cada dois minutos.
        /// </summary>
        public void ObserveErrorLine(string line)
        {
            if (line.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Please run /login", StringComparison.OrdinalIgnoreCase))
            {
                FailureCode = "executor.authentication_required";
            }
        }

        public void Complete()
        {
        }

        public IEnumerable<ExternalAgentEvent> ParseLine(string line)
        {
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(line);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Linha não estruturada: a CLI imprime a falta de credencial assim, FORA do
                // envelope stream-json ("Not logged in · Please run /login"), e sai com código
                // 1. Sem esta tradução o classificador recebia só exit_code_1 e derrubava a
                // conta como transitória — reeleição a cada dois minutos, para sempre.
                if (line.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Please run /login", StringComparison.OrdinalIgnoreCase))
                {
                    FailureCode = "executor.authentication_required";
                }

                // Linha não estruturada (banner, aviso do terminal): ignorada de propósito.
                yield break;
            }

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                yield break;
            }

            if (root.TryGetProperty("session_id", out var session) &&
                session.ValueKind == JsonValueKind.String)
            {
                SessionId ??= session.GetString();
            }

            switch (typeElement.GetString())
            {
                case "system" when Subtype(root) == "init":
                    yield return new ExternalAgentEvent(
                        ExternalAgentEventKind.Started, SessionId: SessionId);
                    break;

                case "assistant":
                    foreach (var @event in ParseAssistant(root))
                    {
                        yield return @event;
                    }

                    break;

                case "rate_limit_event":
                    if (root.TryGetProperty("rate_limit_info", out var limit) &&
                        limit.TryGetProperty("status", out var status) &&
                        status.ValueKind == JsonValueKind.String)
                    {
                        // Cota é sinal de primeira classe: `QuotaLimited` nunca é inferido
                        // da ausência de resposta.
                        yield return new ExternalAgentEvent(
                            ExternalAgentEventKind.Quota, Code: $"quota.{status.GetString()}");
                    }

                    break;

                case "result":
                    foreach (var @event in ParseResult(root))
                    {
                        yield return @event;
                    }

                    break;

                default:
                    break;
            }
        }

        private static string? Subtype(JsonElement root) =>
            root.TryGetProperty("subtype", out var subtype) && subtype.ValueKind == JsonValueKind.String
                ? subtype.GetString()
                : null;

        private static IEnumerable<ExternalAgentEvent> ParseAssistant(JsonElement root)
        {
            if (!root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var blockType) ||
                    blockType.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                switch (blockType.GetString())
                {
                    case "text" when block.TryGetProperty("text", out var text) &&
                        text.ValueKind == JsonValueKind.String:
                        yield return new ExternalAgentEvent(
                            ExternalAgentEventKind.Delta,
                            ExternalAgentRedaction.Redact(text.GetString()));
                        break;

                    case "tool_use" when block.TryGetProperty("name", out var name) &&
                        name.ValueKind == JsonValueKind.String:
                        // Só o NOME da ferramenta: a entrada pode conter conteúdo sensível.
                        yield return new ExternalAgentEvent(
                            ExternalAgentEventKind.ToolUse, Code: name.GetString());
                        break;

                    default:
                        break;
                }
            }
        }

        private IEnumerable<ExternalAgentEvent> ParseResult(JsonElement root)
        {
            var isError = root.TryGetProperty("is_error", out var error) &&
                error.ValueKind == JsonValueKind.True;
            var subtype = Subtype(root);
            var successSubtype = string.Equals(subtype, "success", StringComparison.Ordinal);
            var errorSubtype = subtype?.StartsWith("error", StringComparison.Ordinal) == true;

            if (root.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.String)
            {
                FinalMessage = result.GetString();
            }

            // A falta de credencial veste QUALQUER envelope — inclusive o sucesso contraditório
            // (subtype=success com is_error=true). Observado no contêiner: "Invalid API key ·
            // Please run /login" num envelope de sucesso, que a regra da contradição GLM
            // aceitaria como trabalho concluído. A frase exata da CLI vence o envelope — sem
            // isto a conta caía como transitória e voltava à eleição a cada dois minutos.
            if (FinalMessage?.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) == true ||
                FinalMessage?.Contains("Please run /login", StringComparison.OrdinalIgnoreCase) == true)
            {
                FailureCode = "executor.authentication_required";
                yield return new ExternalAgentEvent(
                    ExternalAgentEventKind.Failed, Code: FailureCode);
                yield break;
            }

            Usage = ReadUsage(root);
            if (Usage is not null)
            {
                yield return new ExternalAgentEvent(ExternalAgentEventKind.Usage, Usage: Usage);
            }

            // O endpoint GLM compatível observado em produção devolve, de forma
            // contraditória, `is_error=true` junto de `subtype=success`, depois de uma mensagem
            // assistant com stop_sequence e de trabalho persistido. O subtipo é o desfecho
            // canônico do envelope; priorizá-lo evita jogar fora commits válidos. O inverso
            // também é fail-closed: subtipo `error_*` falha mesmo se o booleano vier incorreto.
            if (errorSubtype || (isError && !successSubtype))
            {
                FailureCode = $"executor.result_{subtype ?? "error"}";
                yield return new ExternalAgentEvent(
                    ExternalAgentEventKind.Failed, Code: FailureCode);
                yield break;
            }

            // O stream pode conter um envelope transitório antes do resultado final. Um
            // `success` final coerente precisa limpar a falha anterior, ou CollectAsync ainda
            // classificaria a sessão inteira como Failed.
            FailureCode = null;
            yield return new ExternalAgentEvent(
                ExternalAgentEventKind.Completed,
                ExternalAgentRedaction.Redact(FinalMessage),
                SessionId,
                Usage);
        }

        private static ExternalAgentUsage? ReadUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new ExternalAgentUsage(
                ReadLong(usage, "input_tokens"),
                ReadLong(usage, "cache_read_input_tokens"),
                ReadLong(usage, "output_tokens"),
                root.TryGetProperty("total_cost_usd", out var cost) &&
                    cost.ValueKind == JsonValueKind.Number
                    ? cost.GetDecimal()
                    : null,
                root.TryGetProperty("num_turns", out var turns) &&
                    turns.ValueKind == JsonValueKind.Number
                    ? turns.GetInt32()
                    : null);
        }

        private static long? ReadLong(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt64()
                : null;
    }
}
