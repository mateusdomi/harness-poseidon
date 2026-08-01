namespace Harness.Modules.Agents.Application.Execution;

/// <summary>
/// B14 (Fase 2B) — a INTENÇÃO de um turno da chefe.
///
/// O turno é o laço mais quente do produto e era inteiramente livre: o modelo lia a mensagem e
/// decidia, no mesmo fôlego, o que responder E quais ações tomar. Duas mensagens equivalentes
/// podiam render rotas diferentes, porque nada além do juízo do modelo definia a rota — e juízo de
/// modelo não é reproduzível nem testável.
///
/// A divisão é a do princípio determinístico: <b>o modelo decide o quê; o sistema decide como e se
/// pode</b>. O modelo classifica a mensagem numa destas intenções e produz o conteúdo; a sequência
/// de passos e o conjunto de ações permitidas saem da tabela de despacho, que é código.
/// </summary>
public enum ChiefTurnIntent
{
    /// <summary>Não classificada — cai no turno livre com evento auditado. Nunca é "quase isso".</summary>
    Unmatched = 0,

    /// <summary>O dono pediu trabalho novo: decompor em demandas e planejar.</summary>
    PlanejarDemanda,

    /// <summary>Pergunta sobre o produto, o projeto ou uma decisão já tomada.</summary>
    ResponderPergunta,

    /// <summary>Pedido de panorama: o que andou, o que travou, o que vem.</summary>
    ResumirProgresso,

    /// <summary>Algo travou e a decisão de escalar (ou não) precisa ser tomada.</summary>
    DecidirEscalacao,

    /// <summary>Aprovação ou reprovação de um documento submetido.</summary>
    AprovarDocumento,

    /// <summary>Decisão sobre transição de fase da esteira.</summary>
    DecidirGateDeFase,

    /// <summary>Obstáculo fora do alcance da fábrica (acesso, credencial, terceiro).</summary>
    TratarBarreiraExterna,

    /// <summary>Mudança de parâmetro do projeto: prazo, objetivo, marca.</summary>
    AjustarProjeto,

    /// <summary>Pergunta sobre uma pessoa ou sobre a equipe do projeto.</summary>
    PedirStatusPessoaEquipe,

    /// <summary>Conversa que não pede ação: saudação, agradecimento, comentário.</summary>
    ConversaGeral,
}

/// <summary>
/// As AÇÕES que um turno pode produzir. É o vocabulário fechado sobre o qual a tabela de despacho
/// concede permissão — sem isso, "permitir" e "negar" seriam texto solto.
/// </summary>
[Flags]
public enum ChiefTurnAction
{
    /// <summary>Só responde. Todo turno pode.</summary>
    None = 0,

    /// <summary>Criar demandas, que a materialização transformará em cards.</summary>
    CreateDemands = 1,

    /// <summary>Formar ou reorganizar a equipe (criar/ajustar persona).</summary>
    ManageTeam = 2,
}

/// <summary>
/// A tabela de despacho: intenção → o que aquele turno pode fazer e o que precisa carregar.
///
/// É deliberadamente uma tabela, e não um `if` espalhado pelo worker: a rota de cada intenção
/// precisa ser legível de uma vez, comparável entre versões e testável sem subir um modelo.
/// </summary>
public sealed record ChiefIntentRoute(
    ChiefTurnIntent Intent,
    ChiefTurnAction AllowedActions,
    /// <summary>
    /// O contexto que o turno daquela intenção precisa. Não é decoração: carregar o board inteiro
    /// para responder "bom dia" é o custo que o B14 existe para cortar.
    /// </summary>
    bool RequiresBoardSnapshot,
    bool RequiresPhaseState,
    /// <summary>Como a resposta deve ser moldada — o formato é fixo, a prosa é do modelo.</summary>
    string ResponseShape);

public static class ChiefIntentDispatchTable
{
    /// <summary>
    /// Confiança mínima para aceitar a classificação do modelo. Abaixo disto o turno cai no
    /// caminho livre e o evento `intent.unmatched` é registrado — expandir a taxonomia com dado
    /// real é melhor do que forçar a mensagem na gaveta mais parecida.
    /// </summary>
    public const double MinimumConfidence = 0.6;

    private static readonly IReadOnlyDictionary<ChiefTurnIntent, ChiefIntentRoute> Routes =
        new Dictionary<ChiefTurnIntent, ChiefIntentRoute>
        {
            [ChiefTurnIntent.PlanejarDemanda] = new(
                ChiefTurnIntent.PlanejarDemanda,
                ChiefTurnAction.CreateDemands | ChiefTurnAction.ManageTeam,
                RequiresBoardSnapshot: true,
                RequiresPhaseState: true,
                "Confirma o que entendeu, lista as demandas propostas e diz o próximo passo."),

            [ChiefTurnIntent.ResponderPergunta] = new(
                ChiefTurnIntent.ResponderPergunta,
                ChiefTurnAction.None,
                RequiresBoardSnapshot: true,
                RequiresPhaseState: false,
                "Responde direto, cita a fonte do que afirma e separa fato de inferência."),

            [ChiefTurnIntent.ResumirProgresso] = new(
                ChiefTurnIntent.ResumirProgresso,
                ChiefTurnAction.None,
                RequiresBoardSnapshot: true,
                RequiresPhaseState: true,
                "O que andou, o que travou e o que vem — nesta ordem, sem número sem fonte."),

            [ChiefTurnIntent.DecidirEscalacao] = new(
                ChiefTurnIntent.DecidirEscalacao,
                ChiefTurnAction.CreateDemands,
                RequiresBoardSnapshot: true,
                RequiresPhaseState: true,
                "Nomeia o obstáculo, o que já tentou, as alternativas e a decisão que pede ao dono."),

            [ChiefTurnIntent.AprovarDocumento] = new(
                ChiefTurnIntent.AprovarDocumento,
                ChiefTurnAction.None,
                RequiresBoardSnapshot: false,
                RequiresPhaseState: true,
                "Diz o veredito, o critério que o sustenta e o que muda a partir dele."),

            [ChiefTurnIntent.DecidirGateDeFase] = new(
                ChiefTurnIntent.DecidirGateDeFase,
                ChiefTurnAction.None,
                RequiresBoardSnapshot: true,
                RequiresPhaseState: true,
                "Enuncia o critério do gate, a evidência de cada item e o veredito Default-FAIL."),

            [ChiefTurnIntent.TratarBarreiraExterna] = new(
                ChiefTurnIntent.TratarBarreiraExterna,
                ChiefTurnAction.None,
                RequiresBoardSnapshot: false,
                RequiresPhaseState: false,
                "Descreve a barreira, o que está parado por causa dela e a ação exata que pede ao dono."),

            [ChiefTurnIntent.AjustarProjeto] = new(
                ChiefTurnIntent.AjustarProjeto,
                ChiefTurnAction.None,
                RequiresBoardSnapshot: false,
                RequiresPhaseState: false,
                "Confirma o ajuste pedido, o efeito no que já está planejado e pede confirmação."),

            [ChiefTurnIntent.PedirStatusPessoaEquipe] = new(
                ChiefTurnIntent.PedirStatusPessoaEquipe,
                ChiefTurnAction.None,
                RequiresBoardSnapshot: true,
                RequiresPhaseState: false,
                "Diz em que cada especialidade está trabalhando, sem expor conta, modelo ou termo técnico."),

            [ChiefTurnIntent.ConversaGeral] = new(
                ChiefTurnIntent.ConversaGeral,
                ChiefTurnAction.None,
                RequiresBoardSnapshot: false,
                RequiresPhaseState: false,
                "Responde com naturalidade e brevidade. Não inventa trabalho a partir de conversa."),

            // Não classificado: turno livre, como era antes do B14, e nenhuma ação automática.
            // Rebaixar para "sem ação" é o que impede uma classificação falha de virar trabalho
            // que ninguém pediu.
            [ChiefTurnIntent.Unmatched] = new(
                ChiefTurnIntent.Unmatched,
                ChiefTurnAction.None,
                RequiresBoardSnapshot: true,
                RequiresPhaseState: false,
                "Responde ao que foi dito. Se algo pede ação, descreve a ação e pergunta antes."),
        };

    /// <summary>Rota da intenção. Intenção desconhecida cai na rota de <c>Unmatched</c>.</summary>
    public static ChiefIntentRoute For(ChiefTurnIntent intent) =>
        Routes.TryGetValue(intent, out var route) ? route : Routes[ChiefTurnIntent.Unmatched];

    /// <summary>
    /// Resolve a intenção EFETIVA a partir do que o modelo devolveu.
    ///
    /// Confiança abaixo do mínimo vira <c>Unmatched</c> — e isso é uma decisão de produto, não uma
    /// precaução: aceitar classificação incerta transformaria um palpite do modelo numa rota com
    /// permissão de criar trabalho.
    /// </summary>
    public static ChiefTurnIntent Resolve(ChiefTurnIntent classified, double confidence) =>
        classified == ChiefTurnIntent.Unmatched || confidence < MinimumConfidence
            ? ChiefTurnIntent.Unmatched
            : classified;

    /// <summary>Nome canônico da intenção como aparece no contrato e no ledger (snake_case).</summary>
    public static string Name(ChiefTurnIntent intent) => intent switch
    {
        ChiefTurnIntent.PlanejarDemanda => "planejar_demanda",
        ChiefTurnIntent.ResponderPergunta => "responder_pergunta",
        ChiefTurnIntent.ResumirProgresso => "resumir_progresso",
        ChiefTurnIntent.DecidirEscalacao => "decidir_escalacao",
        ChiefTurnIntent.AprovarDocumento => "aprovar_documento",
        ChiefTurnIntent.DecidirGateDeFase => "decidir_gate_de_fase",
        ChiefTurnIntent.TratarBarreiraExterna => "tratar_barreira_externa",
        ChiefTurnIntent.AjustarProjeto => "ajustar_projeto",
        ChiefTurnIntent.PedirStatusPessoaEquipe => "pedir_status_pessoa_equipe",
        ChiefTurnIntent.ConversaGeral => "conversa_geral",
        _ => "unmatched",
    };

    /// <summary>
    /// Converte o nome devolvido pelo modelo. Qualquer coisa fora da taxonomia é
    /// <c>Unmatched</c> — inclusive um nome quase certo. "Quase" não é uma rota.
    /// </summary>
    public static ChiefTurnIntent Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "planejar_demanda" => ChiefTurnIntent.PlanejarDemanda,
        "responder_pergunta" => ChiefTurnIntent.ResponderPergunta,
        "resumir_progresso" => ChiefTurnIntent.ResumirProgresso,
        "decidir_escalacao" => ChiefTurnIntent.DecidirEscalacao,
        "aprovar_documento" => ChiefTurnIntent.AprovarDocumento,
        "decidir_gate_de_fase" => ChiefTurnIntent.DecidirGateDeFase,
        "tratar_barreira_externa" => ChiefTurnIntent.TratarBarreiraExterna,
        "ajustar_projeto" => ChiefTurnIntent.AjustarProjeto,
        "pedir_status_pessoa_equipe" => ChiefTurnIntent.PedirStatusPessoaEquipe,
        "conversa_geral" => ChiefTurnIntent.ConversaGeral,
        _ => ChiefTurnIntent.Unmatched,
    };

    /// <summary>Toda a taxonomia, para o prompt e para os testes de cobertura.</summary>
    public static IReadOnlyList<ChiefTurnIntent> All { get; } =
        [.. Routes.Keys.Where(intent => intent != ChiefTurnIntent.Unmatched)];
}
