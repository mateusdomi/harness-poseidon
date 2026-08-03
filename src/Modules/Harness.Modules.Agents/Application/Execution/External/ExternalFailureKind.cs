namespace Harness.Modules.Agents.Application.Execution.External;

/// <summary>
/// O que deu errado, DECLARADO pelo adaptador do executor — não adivinhado por quem consome.
///
/// Existe para encerrar uma classe inteira de defeito. Até 2026-08-03 o adaptador produzia um
/// código em texto (<c>"executor.quota_exhausted"</c>) e quem decidia fazia
/// <c>code.Contains(sinal)</c> — substring sobre o nosso PRÓPRIO código interno. Foi assim que
/// <c>executor.exit_code_1</c> casou com o sinal <c>"exit_code"</c>, virou "transitório", e uma
/// conta com a sessão esgotada continuou elegível por uma hora e meia batendo na mesma parede.
///
/// A lista de frases nunca fecha: cada fornecedor inventa a sua ("You've hit your session
/// limit", "not supported when using", "Not logged in", "Invalid API key"), e a próxima versão
/// da CLI muda a frase. Reconhecer a frase é trabalho do adaptador, que conhece o fornecedor.
/// Decidir o que fazer é trabalho do núcleo, que não deveria conhecer fornecedor nenhum.
///
/// <see cref="Unknown"/> é honesto e obrigatório: significa "o adaptador não soube dizer", e é
/// o único caso em que a heurística de texto legada ainda vale. Um adaptador que sempre
/// devolvesse <see cref="Unknown"/> não quebraria nada — só não colheria o benefício.
/// </summary>
public enum ExternalFailureKind
{
    /// <summary>Sem falha, ou o adaptador não classificou. Só aqui a heurística legada atua.</summary>
    Unknown = 0,

    /// <summary>Cota/limite da conta esgotado. Tem hora para voltar; insistir antes é desperdício.</summary>
    QuotaExhausted,

    /// <summary>Credencial ausente ou expirada. Só uma pessoa resolve; repetir sozinho não adianta.</summary>
    AuthenticationRequired,

    /// <summary>A conta existe e autentica, mas o plano não serve o modelo pedido.</summary>
    AccountModelUnsupported,

    /// <summary>Instabilidade que passa sozinha: rede, indisponibilidade momentânea do provedor.</summary>
    Transient,

    /// <summary>Nós paramos — reinício do Host, cancelamento, encerramento de sessão.</summary>
    Cancelled,

    /// <summary>Estourou o tempo sem concluir.</summary>
    Timeout,

    /// <summary>Falha real do trabalho: a tentativa chegou a executar e terminou mal.</summary>
    Permanent,
}
