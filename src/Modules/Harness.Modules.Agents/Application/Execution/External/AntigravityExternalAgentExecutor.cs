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

        // CRÍTICO: `--print` CONSOME o próximo argumento como o prompt. Por isso ele é o
        // ÚLTIMO flag — o prompt posicional adicionado pela base vira o VALOR de `--print`.
        // Colocar `--print` no início fazia-o engolir o flag seguinte como "prompt" e o
        // prompt real era ignorado (o modelo respondia sobre os flags). Observado por probe.
        var arguments = new List<string>();

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
            // Critic: `--mode plan` NÃO edita (garante ausência de escrita). Sem
            // `--dangerously-skip-permissions` qualquer ferramenta de LEITURA é auto-negada em
            // headless e o turno morre com "no output produced" — o critic precisa poder ler
            // para revisar. Skip-permissions apenas auto-aprova as tools; o plan mode continua
            // impedindo qualquer escrita, então o critic segue sem capacidade de edição.
            arguments.AddRange(["--mode", "plan", "--dangerously-skip-permissions"]);
        }

        foreach (var directory in request.AdditionalDirectories)
        {
            arguments.AddRange(["--add-dir", directory]);
        }

        // `--print` por ÚLTIMO: o prompt posicional que a base anexa a seguir é o seu valor.
        arguments.Add("--print");
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
    internal sealed class AntigravityTextParser : IExternalAgentOutputParser
    {
        // Sentinelas OBSERVADAS na saída real do `agy` (comparação case-insensitive):
        // "Error: authentication required. Run 'agy' to log in, then retry." e a negação de
        // permissão em modo headless ("no output produced — a tool required the ... permission").
        private static readonly (string Needle, string Code, ExternalFailureKind Kind)[] Sentinels =
        [
            ("authentication required", "executor.authentication_required", ExternalFailureKind.AuthenticationRequired),
            ("authentication failed", "executor.authentication_required", ExternalFailureKind.AuthenticationRequired),
            ("no output produced", "executor.tool_permission_denied", ExternalFailureKind.Permanent),
        ];

        private readonly List<string> _lines = [];

        // `agy --print` não emite o ID da conversa no stdout do modo print; a retomada por
        // `--conversation` depende de um ID conhecido externamente. Nulo é HONESTO aqui.
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

        // O `agy` imprime a falha de autenticação em STDERR e ainda sai com código 0; a
        // sentinela precisa ser observada nos dois fluxos para que a falha seja classificada
        // (e não caia no genérico `executor.no_output`).
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

            // F-26: a CLI `agy` não reporta uso. Em vez de devolver null (que vira
            // usage_unknown/output_tokens=0), estimamos os tokens de saída a partir do texto
            // gerado. A estimativa é conservadora e declarada como aproximada; o custo real
            // continua dependendo do modelo e do tokenizer do provedor.
            if (joined.Length > 0)
            {
                var estimatedOutputTokens = Math.Max(1, joined.Length / 4);
                Usage = new ExternalAgentUsage(null, null, estimatedOutputTokens, null, null);
            }

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
