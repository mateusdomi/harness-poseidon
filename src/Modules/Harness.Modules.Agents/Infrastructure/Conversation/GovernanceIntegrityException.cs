namespace Harness.Modules.Agents.Infrastructure.Conversation;

/// <summary>
/// A governança da chefe não pôde ser carregada íntegra. É exceção TERMINAL de turno por decisão:
/// uma chefe operando com governança ausente ou adulterada é pior do que uma chefe parada, porque
/// a segunda é visível e a primeira só aparece no comportamento.
/// </summary>
public sealed class GovernanceIntegrityException(string message) : InvalidOperationException(message);
