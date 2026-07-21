using System.Text.Json;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;

namespace Harness.Modules.Agents.Application.Execution.External;

/// <summary>
/// Adapter real do Codex CLI (CA-4). É o executor do worker de frontend.
///
/// Flags e formato observados na CLI instalada (0.144.6): `codex exec --json` emite JSONL
/// com `thread.started` (carregando `thread_id`, que é a sessão), `item.completed` (com
/// `item.type` e `item.text`) e `turn.completed` (com `usage`). `--output-last-message`
/// grava a mensagem final num arquivo, que é a fonte AUTORITATIVA do resultado — não se
/// depende de adivinhar qual item era a resposta.
///
/// A retomada usa o subcomando real `codex exec resume &lt;threadId&gt;`.
/// </summary>
public sealed class CodexExternalAgentExecutor(
    ExecutorProfile profile, AccountProfileProvisioner profiles, ExecutorProbe? probe = null)
    : ProcessExternalAgentExecutor(profile, profiles, probe)
{
    public static CodexExternalAgentExecutor Create(
        AccountProfileProvisioner profiles, ExecutorProbe? probe = null) =>
        new(ExecutorCatalog.Find(ExecutorCatalog.Codex)!, profiles, probe);

    public override IReadOnlyList<string> BuildArguments(
        ExternalAgentRunRequest request, ExternalAgentRunContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var arguments = new List<string> { "exec" };
        if (request.ResumeSessionId is { Length: > 0 } sessionId)
        {
            arguments.AddRange(["resume", sessionId]);
        }

        arguments.AddRange(["--json", "--skip-git-repo-check"]);
        arguments.AddRange(["-C", request.WorkingDirectory]);
        arguments.AddRange([
            "--sandbox",
            request.Access == ExternalAgentAccess.Workspace ? "workspace-write" : "read-only",
        ]);
        arguments.AddRange(["--output-last-message", context.LastMessagePath]);

        if (request.Model is { Length: > 0 } model)
        {
            arguments.AddRange(["-m", model]);
        }

        foreach (var directory in request.AdditionalDirectories)
        {
            arguments.AddRange(["--add-dir", directory]);
        }

        // `-` faz o Codex ler as instruções do STDIN, mantendo o prompt fora do argv.
        arguments.Add("-");
        return arguments;
    }

    private protected override IExternalAgentOutputParser CreateParser(
        ExternalAgentRunRequest request, ExternalAgentRunContext context) =>
        new CodexJsonlParser(context.LastMessagePath);

    protected override IReadOnlyList<string> TemporaryPaths(ExternalAgentRunContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [context.LastMessagePath];
    }

    /// <summary>Parser do JSONL do `codex exec --json`, com campos observados na CLI real.</summary>
    private sealed class CodexJsonlParser(string lastMessagePath) : IExternalAgentOutputParser
    {
        public string? SessionId { get; private set; }

        public string? FinalMessage { get; private set; }

        public ExternalAgentUsage? Usage { get; private set; }

        public string? FailureCode { get; private set; }

        public void Complete()
        {
            // A mensagem final autoritativa é o arquivo escrito pela própria CLI; o stream
            // serve para progresso, não para reconstruir a resposta.
            if (!File.Exists(lastMessagePath))
            {
                return;
            }

            try
            {
                var content = File.ReadAllText(lastMessagePath).Trim();
                if (content.Length > 0)
                {
                    FinalMessage = content;
                }
            }
            catch (IOException)
            {
                // Arquivo removido pelo cleanup concorrente: mantém o texto do stream.
            }
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
                yield break;
            }

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                yield break;
            }

            switch (typeElement.GetString())
            {
                case "thread.started":
                    if (root.TryGetProperty("thread_id", out var thread) &&
                        thread.ValueKind == JsonValueKind.String)
                    {
                        SessionId = thread.GetString();
                    }

                    yield return new ExternalAgentEvent(
                        ExternalAgentEventKind.Started, SessionId: SessionId);
                    break;

                case "item.completed":
                    foreach (var @event in ParseItem(root))
                    {
                        yield return @event;
                    }

                    break;

                case "turn.completed":
                    Usage = ReadUsage(root);
                    if (Usage is not null)
                    {
                        yield return new ExternalAgentEvent(
                            ExternalAgentEventKind.Usage, Usage: Usage);
                    }

                    yield return new ExternalAgentEvent(
                        ExternalAgentEventKind.Completed, SessionId: SessionId, Usage: Usage);
                    break;

                case "turn.failed":
                    FailureCode = "executor.turn_failed";
                    yield return new ExternalAgentEvent(
                        ExternalAgentEventKind.Failed, Code: FailureCode);
                    break;

                default:
                    break;
            }
        }

        private IEnumerable<ExternalAgentEvent> ParseItem(JsonElement root)
        {
            if (!root.TryGetProperty("item", out var item) ||
                item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("type", out var itemType) ||
                itemType.ValueKind != JsonValueKind.String)
            {
                yield break;
            }

            var kind = itemType.GetString();
            if (kind == "agent_message")
            {
                if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    FinalMessage = text.GetString();
                    yield return new ExternalAgentEvent(
                        ExternalAgentEventKind.Delta,
                        ExternalAgentRedaction.Redact(FinalMessage));
                }

                yield break;
            }

            // Demais itens (comando, alteração de arquivo, raciocínio) entram apenas como
            // TIPO: o conteúdo pode carregar segredo lido do workspace.
            yield return new ExternalAgentEvent(ExternalAgentEventKind.ToolUse, Code: kind);
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
                ReadLong(usage, "cached_input_tokens"),
                ReadLong(usage, "output_tokens"),
                // O Codex CLI não publica custo: nulo significa DESCONHECIDO, nunca zero.
                CostUsd: null,
                TurnCount: null);
        }

        private static long? ReadLong(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt64()
                : null;
    }
}
