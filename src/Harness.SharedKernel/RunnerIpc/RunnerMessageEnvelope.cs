using System.Text.Json;

namespace Harness.SharedKernel.RunnerIpc;

/// <summary>
/// Mensagem de um runner sobre uma tentativa.
///
/// <see cref="RunnerId"/> identifica o PROCESSO e serve para rotear e diagnosticar — ele nunca
/// decide de quem é o trabalho. Quem decide é <see cref="FencingToken"/>, emitido pelo despacho:
/// é ele que distingue "a mesma tentativa, outro processo" (agente reiniciado, aceito) de "outra
/// tentativa, resultado tardio" (rejeitado). Ver <c>DispatchAuthorityPolicy</c>.
///
/// O token é opcional para não quebrar runners em voo; ausente (0) mantém o comportamento antigo
/// de aceitar, porque recusar uma mensagem legítima é pior do que aceitar sem a prova extra.
/// </summary>
public sealed record RunnerMessageEnvelope(
    string RunnerId,
    string AttemptId,
    long Sequence,
    string IdempotencyKey,
    string Type,
    JsonElement Payload,
    long FencingToken = 0);
