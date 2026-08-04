using Harness.Modules.Workflows.Product;

namespace Harness.Host.Product;

/// <summary>
/// O que um verificador precisa saber: a decisão técnica do projeto, onde a entrega está e sobre
/// qual commit ela está sendo julgada.
/// </summary>
public sealed record ProductVerificationContext(
    ProjectEffectiveProfile Profile,
    string WorkspaceRoot,
    string CommitSha,
    string ProjectId,
    string? AttemptId,
    string? CardId);

/// <summary>
/// Uma verificação independente do modelo. Cada implementação sabe UMA coisa e diz se ela se
/// aplica ao perfil — em vez de um serviço único que conhece todos os frameworks e cresce a cada
/// stack nova.
///
/// O verificador NUNCA recebe comando de agente: ele deriva o que executar do perfil efetivo e dos
/// manifestos reais que encontra na worktree, e chama o <see cref="TrustedProcessRunner"/>, cuja
/// allowlist de executáveis é fechada no código.
/// </summary>
public interface IProductVerifier
{
    /// <summary>Que evidência esta verificação produz.</summary>
    ProductEvidenceKind Kind { get; }

    /// <summary>Nome estável do verificador, registrado na proveniência da evidência.</summary>
    string Name { get; }

    /// <summary>Este verificador tem o que fazer neste perfil?</summary>
    bool AppliesTo(ProjectEffectiveProfile profile);

    /// <summary>
    /// Executa. Devolve <see langword="null"/> quando, apesar de aplicável ao perfil, a entrega não
    /// oferece o que verificar (nenhum projeto, nenhum manifesto) — a ausência de evidência é lida
    /// pelo gate como reprovação, nunca como passagem, e inventar um resultado aqui apagaria a
    /// diferença entre "não verificado" e "verificado e passou".
    /// </summary>
    Task<ProductVerificationRecord?> VerifyAsync(
        ProductVerificationContext context,
        CancellationToken cancellationToken);
}
