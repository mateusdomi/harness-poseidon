using System.Globalization;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;

namespace Harness.Modules.Agents.Application.Execution.External;

/// <summary>
/// Adapter real do Antigravity CLI (`agy`) (CA-4/N3). É o executor de vocação CRITIC.
///
/// Antigravity NÃO é experimental: a CLI está instalada (1.1.5) e é usada em produção pelo
/// operador para code review. Todas as flags e comportamentos abaixo foram OBSERVADOS por
/// probe real na máquina, nunca inventados:
///
/// - <c>--print</c> (alias <c>-p</c>/<c>--prompt</c>) roda um prompt único não interativo e
///   imprime a resposta em TEXTO puro — não há modo JSON/stream (por isso
///   <see cref="CapabilitySet.SupportsStreaming"/> é falso para este executor).
/// - O prompt é entregue como ARGUMENTO POSICIONAL: `agy --print "&lt;prompt&gt;"`. A CLI não
///   lê o prompt do STDIN — é uma limitação real do binário, declarada em
///   <see cref="PromptDelivery"/>.
/// - <c>--print-timeout &lt;dur&gt;</c> limita a espera (padrão 5m0s).
/// - <c>--conversation &lt;id&gt;</c> retoma uma conversa; <c>--model</c> e
///   <c>--effort (low|medium|high)</c> selecionam modelo/esforço.
/// - <c>--mode plan</c> é o modo somente-plano (não edita); <c>--mode accept-edits</c> mais
///   <c>--dangerously-skip-permissions</c> é o modo de escrita não interativo. Em modo
///   headless uma ferramenta que peça permissão é AUTO-NEGADA se `--dangerously-skip-permissions`
///   não for passado — o critic fica assim fail-closed por construção.
/// - Config home / autenticação vivem em <c>$HOME/.gemini/antigravity-cli</c>. Não existe
///   variável dedicada de config home: o isolamento da conta é por HOME (CA-3), e o login
///   é interativo (`agy`), portanto o perfil isolado exige OAuth humano — sem ele o smoke
///   live é BLOCKED_EXTERNAL_OAUTH.
/// - Falha de autenticação e negação de permissão saem com EXIT CODE 0. Por isso a detecção
///   de falha é pela SAÍDA (sentinela + resposta vazia), nunca pelo código de saída.
/// </summary>
public sealed class AntigravityExternalAgentExecutor(
    ExecutorProfile profile, AccountProfileProvisioner profiles, ExecutorProbe? probe = null)
    : ProcessExternalAgentExecutor(profile, profiles, probe)
{
    public static AntigravityExternalAgentExecutor Create(
        AccountProfileProvisioner profiles, ExecutorProbe? probe = null) =>
        new(ExecutorCatalog.Find(ExecutorCatalog.Antigravity)!, profiles, probe);

    /// <summary>`agy --print` só aceita o prompt como argumento posicional (probe real).</summary>
    protected override ExternalPromptDelivery PromptDelivery => ExternalPromptDelivery.PositionalArgument;

    public override IReadOnlyList<string> BuildArguments(
        ExternalAgentRunRequest request, ExternalAgentRunContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var arguments = new List<string> { "--print" };

        // Timeout em duração aceita pelo flag Go (`1800s`), derivado do request.
        arguments.AddRange([
            "--print-timeout",
            $"{(long)request.Timeout.TotalSeconds}s",
        ]);

        if (request.ResumeSessionId is { Length: > 0 } conversationId)
        {
            arguments.AddRange(["--conversation", conversationId]);
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
            // Actor: pode editar; em headless as permissões precisam ser auto-aprovadas.
            arguments.AddRange(["--mode", "accept-edits", "--dangerously-skip-permissions"]);
        }
        else
        {
            // Critic: modo plano NÃO edita e o sandbox restringe o terminal. Sem
            // `--dangerously-skip-permissions`, qualquer ferramenta é auto-negada — o critic
            // é fail-closed por construção e nunca recebe capacidade de escrita.
            arguments.AddRange(["--mode", "plan", "--sandbox"]);
        }

        foreach (var directory in request.AdditionalDirectories)
        {
            arguments.AddRange(["--add-dir", directory]);
        }

        return arguments;
    }

    private protected override IExternalAgentOutputParser CreateParser(
        ExternalAgentRunRequest request, ExternalAgentRunContext context) =>
        new AntigravityTextParser();

    /// <summary>
    /// Parser da saída em TEXTO puro do `agy --print`. Não há envelope estruturado: a
    /// resposta é o stdout acumulado. A falha é detectada pela SAÍDA — sentinela de
    /// autenticação/negação de permissão, ou resposta vazia — porque o `agy` sai com exit
    /// code 0 mesmo quando não autentica.
    /// </summary>
    private sealed class AntigravityTextParser : IExternalAgentOutputParser
    {
        // Sentinelas OBSERVADAS na saída real do `agy` (comparação case-insensitive):
        // "Error: authentication required. Run 'agy' to log in, then retry." e a negação de
        // permissão em modo headless ("no output produced — a tool required the ... permission").
        private static readonly (string Needle, string Code)[] Sentinels =
        [
            ("authentication required", "executor.authentication_required"),
            ("authentication failed", "executor.authentication_required"),
            ("no output produced", "executor.tool_permission_denied"),
        ];

        private readonly List<string> _lines = [];

        // `agy --print` não emite o ID da conversa no stdout do modo print; a retomada por
        // `--conversation` depende de um ID conhecido externamente. Nulo é HONESTO aqui.
        public string? SessionId => null;

        public string? FinalMessage { get; private set; }

        public ExternalAgentUsage? Usage => null;

        public string? FailureCode { get; private set; }

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

        // O `agy` imprime a falha de autenticação em STDERR e ainda sai com código 0; a
        // sentinela precisa ser observada nos dois fluxos para que a falha seja classificada
        // (e não caia no genérico `executor.no_output`).
        public void ObserveErrorLine(string line) => MatchSentinel(line);

        private string? MatchSentinel(string line)
        {
            foreach (var (needle, code) in Sentinels)
            {
                if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    FailureCode ??= code;
                    return code;
                }
            }

            return null;
        }

        public void Complete()
        {
            var joined = string.Join('\n', _lines).Trim();
            FinalMessage = joined;

            // Resposta vazia sem sentinela é ainda assim uma NÃO-resposta: fail-closed. O
            // `agy` sai 0 mesmo sem produzir texto (auth em stderr, quota, negação), então a
            // ausência de resposta nunca pode ser lida como sucesso.
            if (FailureCode is null && joined.Length == 0)
            {
                FailureCode = "executor.no_output";
            }
        }
    }
}
