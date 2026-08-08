using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;

namespace Harness.Modules.Agents.Application.Execution.External;

/// <summary>
/// Adapter real do Kimi Code CLI (`kimi`) (CA-4). Executor de vocação frontend/executor.
///
/// O dono usa `kimi --yolo` interativamente; para execução governada e não interativa, a CLI
/// (probe real, binário em <c>~/.kimi-code/bin/kimi</c>) oferece:
/// - <c>-p, --prompt &lt;prompt&gt;</c>: roda UM prompt não interativo e imprime a resposta.
/// - <c>--auto</c>: modo de permissão totalmente autônomo — o agente NÃO faz perguntas
///   (equivalente headless do <c>--yolo</c>, que só auto-aprova mas ainda pode perguntar).
/// - <c>--output-format text|stream-json</c>: usamos <c>text</c> (a resposta é o stdout).
/// - <c>-m, --model &lt;alias&gt;</c>: seleciona o modelo; sem ele usa o default do config.toml.
///
/// Isolamento por HOME (CA-3), como o Antigravity: a autenticação vive sob o HOME do config
/// home do alias. Falha de autenticação/cota é detectada pela SAÍDA (sentinela), não pelo exit
/// code — o mesmo cuidado fail-closed dos demais executores de CLI.
/// </summary>
public sealed class KimiExternalAgentExecutor(
    ExecutorProfile profile, AccountProfileProvisioner profiles, ExecutorProbe? probe = null)
    : ProcessExternalAgentExecutor(profile, profiles, probe)
{
    public static KimiExternalAgentExecutor Create(
        AccountProfileProvisioner profiles, ExecutorProbe? probe = null) =>
        new(ExecutorCatalog.Find(ExecutorCatalog.KimiCode)!, profiles, probe);

    /// <summary>`kimi -p` recebe o prompt como VALOR do flag; a base o anexa após o último flag.</summary>
    protected override ExternalPromptDelivery PromptDelivery => ExternalPromptDelivery.PositionalArgument;

    public override IReadOnlyList<string> BuildArguments(
        ExternalAgentRunRequest request, ExternalAgentRunContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        // `-p` já é não-interativo e NÃO combina com --auto/--yolo (a CLI recusa). Em modo
        // prompt não há aprovação de ferramenta pendente — o prompt roda e imprime a resposta.
        var arguments = new List<string>
        {
            "--output-format",
            "text",
        };

        if (request.Model is { Length: > 0 } model)
        {
            arguments.AddRange(["--model", model]);
        }

        // `-p` por ÚLTIMO: o prompt que a base anexa a seguir vira o VALOR de `--prompt`.
        arguments.Add("-p");
        return arguments;
    }

    private protected override IExternalAgentOutputParser CreateParser(
        ExternalAgentRunRequest request, ExternalAgentRunContext context) =>
        new KimiTextParser();

    /// <summary>
    /// Parser da saída em TEXTO do `kimi -p`. A resposta é o stdout acumulado; a falha é pela
    /// SAÍDA (sentinela de cota/autenticação), fail-closed contra resposta vazia.
    /// </summary>
    internal sealed class KimiTextParser : IExternalAgentOutputParser
    {
        private static readonly (string Needle, string Code, ExternalFailureKind Kind)[] Sentinels =
        [
            ("usage limit", "executor.quota_exhausted", ExternalFailureKind.QuotaExhausted),
            ("rate limit", "executor.quota_exhausted", ExternalFailureKind.QuotaExhausted),
            ("quota", "executor.quota_exhausted", ExternalFailureKind.QuotaExhausted),
            ("authentication required", "executor.authentication_required", ExternalFailureKind.AuthenticationRequired),
            ("not logged in", "executor.authentication_required", ExternalFailureKind.AuthenticationRequired),
            ("please log in", "executor.authentication_required", ExternalFailureKind.AuthenticationRequired),
        ];

        private readonly List<string> _lines = [];

        public string? SessionId => null;

        public string? FinalMessage { get; private set; }

        public ExternalAgentUsage? Usage { get; private set; }

        public string? FailureCode { get; private set; }

        public ExternalFailureKind FailureKind { get; private set; } = ExternalFailureKind.Unknown;

        public IEnumerable<ExternalAgentEvent> ParseLine(string line)
        {
            if (MatchSentinel(line) is { } code)
            {
                yield return new ExternalAgentEvent(ExternalAgentEventKind.Failed, Code: code);
                yield break;
            }

            var redacted = ExternalAgentRedaction.Redact(line);
            _lines.Add(redacted);
            if (redacted.Length > 0)
            {
                yield return new ExternalAgentEvent(ExternalAgentEventKind.Delta, redacted);
            }
        }

        public void ObserveErrorLine(string line) => MatchSentinel(line);

        private string? MatchSentinel(string line)
        {
            foreach (var (needle, code, kind) in Sentinels)
            {
                if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    FailureCode ??= code;
                    FailureKind = kind;
                    return code;
                }
            }

            return null;
        }

        public void Complete()
        {
            var joined = string.Join('\n', _lines).Trim();
            FinalMessage = joined;

            if (joined.Length > 0)
            {
                var estimatedOutputTokens = Math.Max(1, joined.Length / 4);
                Usage = new ExternalAgentUsage(null, null, estimatedOutputTokens, null, null);
            }

            if (FailureCode is null && joined.Length == 0)
            {
                FailureCode = "executor.no_output";
            }
        }
    }
}
