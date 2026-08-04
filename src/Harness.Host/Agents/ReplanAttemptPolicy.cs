namespace Harness.Host.Agents;

/// <summary>
/// Uma tentativa EXERCEU a abordagem do card?
///
/// É a pergunta que a guarda do replanejamento faz antes de recusar a volta de um card escalado.
/// Replanejar duas vezes sobre a mesma abordagem só repete o fracasso — mas para isso a abordagem
/// precisa ter sido de fato exercida, e três coisas diferentes se pareciam com isso:
///
/// - morrer por infraestrutura (reinício do Host, conta sem cota) nunca julgou o enunciado;
/// - não produzir token nenhum é o mesmo caso, visto por outro sinal;
/// - <b>rodar, gastar token e não introduzir mudança nenhuma</b> também não exerceu abordagem —
///   não há o que evitar repetir, porque nada foi tentado.
///
/// A terceira é a que custou caro. A primeira versão da regra a mediu por
/// <c>CommitRefs.Count > 0</c>, e esse sinal é sempre verdadeiro: a colheita governada commita os
/// restos da worktree e devolve o HEAD dela mesmo quando não havia resto, então toda tentativa
/// colhida grava um <c>git-commit:</c> — para as vazias, o SHA da própria base. A regra existia,
/// passava nos testes e nunca disparava em produção.
///
/// Por isso a entrega entra aqui como <see cref="bool"/>? e não como contagem: <c>null</c> é
/// "não deu para apurar", e conta como ENTREGOU. Afrouxar uma guarda anti-laço por causa de uma
/// leitura que falhou é o pior default disponível.
/// </summary>
public static class ReplanAttemptPolicy
{
    public static bool ExercisedApproach(
        string? failureReason, long tokensOutput, bool? introducedChanges) =>
        !CardCircuitBreakerService.IsInfrastructureFailure(failureReason) &&
        tokensOutput > 0 &&
        introducedChanges != false;
}
