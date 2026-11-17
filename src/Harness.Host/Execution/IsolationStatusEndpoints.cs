namespace Harness.Host.Execution;

/// <summary>
/// Estado REAL da proteção de isolamento, disponível antes de existir perfil/sessão.
///
/// O onboarding precisava dessa resposta e não tinha onde perguntar: o único diagnóstico
/// (`/api/v1/agent-accounts/doctor`) exige sessão, e o assistente de primeiro uso afirmava
/// "a proteção de isolamento não está disponível" em toda instalação — inclusive com Docker e
/// imagem prontos. Uma tela de segurança que diz sempre a mesma coisa não informa nada.
///
/// A resposta não carrega caminho, conta, imagem nem segredo: só o modo configurado e se a
/// contenção está efetivamente operante, com a mesma mensagem acionável do launcher/doctor.
/// </summary>
public static class IsolationStatusEndpoints
{
    public const string Route = "/api/v1/isolation-status";

    public static IEndpointRouteBuilder MapIsolationStatus(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, (IsolatedExecutionSettings settings) =>
                Results.Ok(Evaluate(
                    settings,
                    ContainerRuntimeProbe.IsAvailable,
                    ContainerRuntimeProbe.HasImage)))
            .WithTags("isolation")
            .Produces<IsolationStatusResponse>();
        return endpoints;
    }

    /// <summary>Avaliação pura (testável sem Docker): o modo manda; o runtime e a imagem provam.</summary>
    public static IsolationStatusResponse Evaluate(
        IsolatedExecutionSettings settings,
        Func<bool> runtimeAvailable,
        Func<string, bool> hasImage)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runtimeAvailable);
        ArgumentNullException.ThrowIfNull(hasImage);

        var mode = settings.Mode switch
        {
            IsolatedExecutionMode.Docker => "docker",
            IsolatedExecutionMode.Fake => "fake",
            _ => "disabled",
        };
        if (settings.Mode != IsolatedExecutionMode.Docker)
        {
            // `fake` existe para testes; não protege nada no host e é tratado como inseguro.
            return new IsolationStatusResponse(mode, false, false, false, null);
        }

        var runtimeUp = runtimeAvailable();
        var imageReady = runtimeUp && hasImage(settings.AgentImageName);
        var message = !runtimeUp
            ? ContainerRuntimeProbe.Message
            : imageReady
                ? null
                : ContainerRuntimeProbe.MissingImage(settings.AgentImageName);
        return new IsolationStatusResponse(mode, runtimeUp, imageReady, runtimeUp && imageReady, message);
    }
}

/// <param name="Mode">Modo configurado: <c>docker</c>, <c>disabled</c> ou <c>fake</c>.</param>
/// <param name="ContainerRuntimeReady">O daemon do contêiner responde.</param>
/// <param name="AgentImageReady">A imagem de execução dos agentes existe localmente.</param>
/// <param name="Protected">A contenção está operante: modo Docker, runtime e imagem prontos.</param>
/// <param name="Message">Instrução acionável quando a contenção configurada não está operante.</param>
public sealed record IsolationStatusResponse(
    string Mode,
    bool ContainerRuntimeReady,
    bool AgentImageReady,
    bool Protected,
    string? Message);
