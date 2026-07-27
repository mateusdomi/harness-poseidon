using System.Globalization;

namespace Harness.Modules.Agents.Application.Accounts;

/// <summary>Por que o checkpoint foi tirado. É a origem que decide se a conta pode mudar.</summary>
public enum CheckpointOrigin
{
    /// <summary>Cota esgotada: continuar EXIGE trocar de conta.</summary>
    Quota,

    /// <summary>Falha transitória: a mesma conta pode voltar, outra compatível também serve.</summary>
    Transient,

    /// <summary>Cancelamento: o trabalho não continua.</summary>
    Cancelled,

    /// <summary>Reprovação do crítico: quem corrige o próprio achado é quem o produziu.</summary>
    Review,
}

/// <summary>Veredito da retomada: se pode continuar, com que fidelidade e por quê.</summary>
public sealed record CheckpointResumeVerdict(
    bool Allowed,
    string ReasonCode,
    /// <summary>
    /// Verdadeiro quando o estado é retomado tal como estava. Falso quando houve RECONSTRUÇÃO
    /// parcial — e nesse caso é proibido afirmar "continuação exata", porque a diferença precisa
    /// ficar registrada em vez de virar promessa não cumprida.
    /// </summary>
    bool ExactContinuation);

/// <summary>
/// Decide se um checkpoint pode ser retomado por OUTRA tentativa, possivelmente em outra conta.
///
/// O modelo anterior amarrava o estado retomável à tentativa e ao ator: a continuação exigia o
/// MESMO alias. Isso contradizia exatamente o caso que precisava cobrir — quando a cota esgota,
/// continuar significa trocar de conta —, então o trabalho parcial era descartado e a nova
/// tentativa recomeçava do zero.
///
/// O que NÃO muda com a troca: o papel, o projeto, o card e o repositório. E a segregação de
/// revisão continua intacta: uma correção pedida pelo crítico permanece com quem produziu o
/// trabalho, porque trocar o ator ali apagaria a autoria do que está sendo corrigido.
/// </summary>
public static class CheckpointResumePolicy
{
    public static CheckpointOrigin ParseOrigin(string? value) =>
        (value ?? string.Empty).Trim().ToLower(CultureInfo.InvariantCulture) switch
        {
            "quota" => CheckpointOrigin.Quota,
            "cancelled" => CheckpointOrigin.Cancelled,
            "review" => CheckpointOrigin.Review,
            _ => CheckpointOrigin.Transient,
        };

    public static string Serialize(CheckpointOrigin origin) => origin switch
    {
        CheckpointOrigin.Quota => "quota",
        CheckpointOrigin.Cancelled => "cancelled",
        CheckpointOrigin.Review => "review",
        _ => "transient",
    };

    /// <param name="executorChanged">
    /// O executor (adapter técnico) mudou, e não apenas a conta. Aí a retomada não é exata: o
    /// formato de sessão e o modelo de edição diferem, e o que se recupera é o trabalho em disco
    /// mais o resumo — nunca a sessão do agente anterior.
    /// </param>
    public static CheckpointResumeVerdict Evaluate(
        CheckpointOrigin origin,
        string checkpointRole,
        string candidateRole,
        string checkpointAccount,
        string candidateAccount,
        bool executorChanged)
    {
        if (origin == CheckpointOrigin.Cancelled)
        {
            // Cancelar é decisão explícita de parar. Retomar por trás dela desfaria a decisão.
            return new CheckpointResumeVerdict(false, "resume.cancelled", false);
        }

        // O PAPEL define o escopo de escrita. Retomar sob outro papel seria mudar silenciosamente
        // o que a tentativa pode tocar.
        if (!string.Equals(checkpointRole, candidateRole, StringComparison.OrdinalIgnoreCase))
        {
            return new CheckpointResumeVerdict(false, "resume.role_mismatch", false);
        }

        var sameAccount = string.Equals(
            checkpointAccount, candidateAccount, StringComparison.OrdinalIgnoreCase);
        if (origin == CheckpointOrigin.Review && !sameAccount)
        {
            // Correção de achado permanece com quem produziu: trocar o ator aqui apagaria a
            // autoria do trabalho que está sendo corrigido e embaralharia a segregação da revisão.
            return new CheckpointResumeVerdict(false, "resume.review_requires_same_actor", false);
        }

        return sameAccount
            ? new CheckpointResumeVerdict(true, "resume.same_account", !executorChanged)
            : new CheckpointResumeVerdict(
                true,
                executorChanged ? "resume.reconstructed_on_other_executor" : "resume.other_account",
                !executorChanged);
    }
}
