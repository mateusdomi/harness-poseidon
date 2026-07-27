namespace Harness.Modules.Agents.Application.Accounts;

/// <summary>
/// Separa o adiamento que o TEMPO resolve do adiamento que só uma AÇÃO resolve.
///
/// O scheduler é fail-closed: quando nenhuma conta serve, ele recusa em vez de escalar o card
/// para qualquer agente disponível. A recusa, porém, tem duas naturezas muito diferentes. Cota
/// esgotada, cooldown, concorrência cheia e orçamento da rodada passam sozinhos — o card sai na
/// próxima janela e avisar seria ruído. Já "nenhuma conta tem este papel", "o executor não tem
/// adapter", "a capacidade não existe", "o escopo não é permitido a ninguém" ou "falta login" não
/// mudam com o relógio: o card fica parado indefinidamente e, de fora, parece backlog normal.
///
/// Esta é uma política PURA justamente para poder ser provada sem subir Host: é ela que decide
/// quando o chefe precisa levar o caso ao dono.
/// </summary>
public static class ChiefDeferralTriage
{
    /// <summary>
    /// Motivos de recusa que nenhuma espera resolve. Conjunto FECHADO e conservador: um código
    /// desconhecido nunca é tratado como estrutural, porque avisar demais treina o dono a ignorar
    /// o aviso — o custo de um falso positivo aqui é a credibilidade do canal.
    /// </summary>
    private static readonly HashSet<string> StructuralRefusals = new(
        [
            "account.role_not_allowed",
            "account.adapter_not_implemented",
            "account.executor_unknown",
            "account.capability_unsupported",
            "account.path_scope_not_allowed",
            "account.authentication_required",
            "account.disabled",
            "account.executor_unavailable",
        ],
        StringComparer.Ordinal);

    /// <summary>
    /// Verdadeiro quando TODAS as contas consideradas recusaram por motivo estrutural. Basta uma
    /// recusa transitória (ou uma conta elegível que só perdeu o slot) para o caso voltar a ser
    /// espera normal: enquanto existir alguém que ainda pode assumir, não há o que anunciar.
    /// Sem candidatos avaliados não há veredito — ausência de dado nunca vira conclusão.
    /// </summary>
    public static bool IsStructural(ChiefDeferral deferral)
    {
        ArgumentNullException.ThrowIfNull(deferral);
        return deferral.Candidates is { Count: > 0 } candidates &&
            candidates.All(candidate =>
                !candidate.Eligible && StructuralRefusals.Contains(candidate.ReasonCode));
    }
}
