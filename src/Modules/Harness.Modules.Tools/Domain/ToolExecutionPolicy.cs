namespace Harness.Modules.Tools.Domain;

public enum ToolRiskTier { Low = 0, Medium = 1, High = 2, Critical = 3 }

public sealed record ToolPolicyDescriptor(
    string ToolId,
    bool Enabled,
    ToolRiskTier MaximumRiskTier,
    string InputSchemaJson,
    string OutputSchemaJson);

public sealed record ToolPolicyContext(
    string PhaseName,
    ToolRiskTier TaskRiskTier,
    IReadOnlySet<string> AllowedToolIds,
    bool SandboxActive,
    /// <summary>
    /// O OPERADOR declarou, na configuração desta instalação, que aceita executar sem a
    /// fronteira do contêiner. Falso por padrão — e é a única forma de dispensar a sandbox.
    ///
    /// Note o que este campo NÃO faz: ele não altera <see cref="SandboxActive"/> nem a
    /// attestation. A evidência continua registrando, com todas as letras, que não houve
    /// contêiner. Só a DECISÃO muda, e muda porque alguém a assinou.
    /// </summary>
    bool UncontainedExecutionAcknowledged = false);

public sealed record ToolInvocationPolicyRequest(
    ToolPolicyDescriptor Tool,
    ToolPolicyContext Context,
    ToolRiskTier InvocationRiskTier);

public sealed record ToolPolicyDecision(bool Allowed, string Code, string Detail)
{
    public static ToolPolicyDecision Permit() => new(true, "allowed", "Tool invocation satisfies the active policy.");

    /// <summary>
    /// Permitido, mas SEM a fronteira do contêiner, por declaração do operador. Código próprio
    /// de propósito: um `allowed` indistinguível esconderia, no relatório e no grep, quantas
    /// execuções aconteceram sem contenção.
    /// </summary>
    public static ToolPolicyDecision PermitUncontained() => new(
        true, "allowed_uncontained",
        "Tool invocation allowed WITHOUT container containment, by explicit operator declaration.");
    public static ToolPolicyDecision Deny(string code, string detail) => new(false, code, detail);
}

public static class ToolExecutionPolicy
{
    public static ToolPolicyDecision Evaluate(ToolInvocationPolicyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Context.PhaseName))
            return ToolPolicyDecision.Deny("phase_required", "A workflow phase is required for tool execution.");
        if (!request.Tool.Enabled)
            return ToolPolicyDecision.Deny("tool_disabled", "The tool is disabled.");
        if (!request.Context.AllowedToolIds.Contains(request.Tool.ToolId))
            return ToolPolicyDecision.Deny("tool_not_allowlisted", "The tool is not allowlisted for the active phase.");
        if (request.InvocationRiskTier > request.Tool.MaximumRiskTier)
            return ToolPolicyDecision.Deny("tool_risk_exceeded", "The invocation exceeds the tool risk ceiling.");
        if (request.InvocationRiskTier > request.Context.TaskRiskTier)
            return ToolPolicyDecision.Deny("task_risk_exceeded", "The invocation exceeds the accepted task risk tier.");
        // Decisão do proprietário (31/07/2026): o contêiner é pré-requisito nos DOIS modos, e
        // nenhum aceite de risco POR CARD o dispensa — uma exceção "temporária" que o produto
        // aceita card a card é uma exceção permanente na prática.
        //
        // Revisada pelo proprietário em 03/08/2026, com uma causa medida: as contas do Claude
        // Code guardam a credencial no Keychain do macOS, que não existe dentro do contêiner. A
        // MESMA conta, com o MESMO config home, responde no host e responde
        // `Invalid API key · Please run /login` no contêiner. O isolamento não estava contendo
        // risco; estava cegando as únicas contas com cota, e a operação parou por isso.
        //
        // A exceção que existe agora é de outra natureza, e a diferença é o que a torna
        // aceitável: ela é da INSTALAÇÃO, não do card. Vale para todo mundo, aparece na
        // configuração, sai com código próprio no registro e some quando o operador a remove.
        // Uma exceção por card se esconde no volume; uma exceção por instalação é uma escolha
        // que alguém precisa manter escrita.
        if (request.InvocationRiskTier >= ToolRiskTier.High && !request.Context.SandboxActive)
        {
            return request.Context.UncontainedExecutionAcknowledged
                ? ToolPolicyDecision.PermitUncontained()
                : ToolPolicyDecision.Deny(
                    "sandbox_required", "High-risk tool execution requires an attested sandbox.");
        }

        return ToolPolicyDecision.Permit();
    }
}
