using Harness.Modules.Coordination.Application;
using System.Text.Json.Serialization;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Governance.Coordination;
using Harness.Persistence.Abstractions.AttemptWorkspaces;

namespace Harness.Host.Agents;

/// <summary>
/// Configuração do bootstrap governado de agentes (CA-5), seção `Harness:AgentRuns`.
///
/// Nasce DESLIGADO: um produto recém-instalado não executa agente externo sem que o
/// operador declare a raiz controlada e habilite o recurso.
/// </summary>
public sealed record AgentRunSettings
{
    public bool Enabled { get; init; }

    /// <summary>Raiz dos perfis isolados por conta (CA-3). Nunca dentro do repositório.</summary>
    public string? ProfilesRoot { get; init; }

    /// <summary>Raiz controlada onde worktrees de tentativa podem existir.</summary>
    public string? ControlledRoot { get; init; }

    /// <summary>Arquivo local de contas; ausente usa os aliases canônicos de ADR-021.</summary>
    public string? AccountsFilePath { get; init; }

    /// <summary>
    /// Arquivo do ledger durável de disponibilidade (cota/cooldown/login) das contas. Ausente
    /// usa `~/.harness/account-availability.json`, o caminho do operador.
    ///
    /// Existe configurável porque, sem isso, QUALQUER Host — inclusive o que sobe dentro de um
    /// teste — lê e escreve o ledger da instalação real do operador: uma conta em cooldown na
    /// máquina reprovava a suíte por um motivo que não está no código, e um teste podia sujar o
    /// estado de produção do dono. Ambiente é configuração, não constante.
    /// </summary>
    public string? AvailabilityLedgerPath { get; init; }

    /// <summary>
    /// Raiz do arquivo de artifacts de tentativas reprovadas (patch + manifest de
    /// provenance), fora do repositório. Ausente usa <c>~/.harness/pilots</c>.
    /// </summary>
    public string? ArchiveRoot { get; init; }

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Silêncio máximo tolerado dentro de um run: sem UMA linha de saída por este tempo, a
    /// execução é encerrada como travada em vez de esperar o <see cref="RunTimeout"/> inteiro.
    /// Zero desliga a vigilância.
    /// </summary>
    public TimeSpan RunNoProgressTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Orçamento do bundle documental de uma execução de agente. Subiu de 8.000: com o canon do
    /// produto registrado e a seleção pelo vocabulário real, o conjunto correto mede perto do
    /// teto antigo, e o corte alcançava a regra de coordenação. Elevar o teto não substitui a
    /// política de faixas — ela é que garante que o obrigatório nunca desapareça em silêncio.
    /// </summary>
    public int ContextTokenBudget { get; init; } = 16000;

    /// <summary>
    /// Loop autônomo do chefe (drena o backlog e delega).
    ///
    /// Fase 1E: nasce LIGADO, e o freio real passou a ser o MODO DO PROJETO. Ele nascia desligado
    /// porque era a única trava existente — e, sendo global, obrigava o dono a escolher entre
    /// automatizar tudo ou não automatizar nada. Numa instalação limpa isso significava que um
    /// projeto declarado autônomo não andava até alguém achar um interruptor, o que contradiz o
    /// próprio modo que o dono escolheu.
    ///
    /// Agora ele é o INTERRUPTOR DO OPERADOR: um kill switch para parar a fábrica inteira quando
    /// preciso. Quem decide se um projeto avança sozinho é o `OperationMode` dele — `manual` exige
    /// disparo humano, e o `AutonomousActionGuard` segue intocado decidindo o que a autonomia pode
    /// fazer depois de permitida. O card continua indo só até <c>AwaitingReview</c>.
    /// </summary>
    public bool AutoDispatchEnabled { get; init; } = true;

    /// <summary>Intervalo entre ciclos do loop do chefe.</summary>
    public TimeSpan AutoDispatchInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Teto de despachos concorrentes por ciclo — trava de segurança do auto-dispatch.</summary>
    public int AutoDispatchMaxConcurrent { get; init; } = 2;

    /// <summary>
    /// Teto de operações PESADAS simultâneas — compilar, rodar suíte, subir aplicação, abrir
    /// navegador.
    ///
    /// O default é 1 e é deliberado para o piloto: uma avaliação de entrega Web já dispara dois
    /// builds, uma suíte, um build de frontend, duas subidas de API, um servidor de interface e um
    /// navegador. Duas em paralelo na mesma máquina não somam — competem, e esta máquina já travou
    /// por isso. Quem souber que a máquina aguenta mais aumenta aqui; o piso efetivo é 1, porque
    /// zero pararia a fábrica sem produzir erro nenhum.
    /// </summary>
    public int MaxConcurrentHeavyOperations { get; init; } = 1;

    /// <summary>
    /// Falhas consecutivas que abrem o circuito de um card. O default é o histórico (3).
    ///
    /// É configurável porque o número certo depende de quanto a infraestrutura da vez está
    /// confiável, e essa é uma informação do OPERADOR, não do código: numa noite em que as
    /// contas caem por motivo externo, três falhas seguidas dizem mais sobre o provedor do que
    /// sobre o enunciado do card. Continua sendo um limiar, e não um interruptor: valor menor
    /// que 1 não desliga o circuito, é recusado no uso.
    /// </summary>
    public int CardCircuitFailureThreshold { get; init; } =
        CardCircuitBreakerPolicy.ConsecutiveFailureThreshold;

    /// <summary>
    /// Tentativas consecutivas SEM PRODUZIR NADA que a esteira tolera num card antes de parar
    /// de insistir. Pergunta diferente do circuito: não acusa o card, só reconhece que
    /// repetir o mesmo fracasso não é progresso.
    ///
    /// Cinco por padrão — acima do limiar de culpa (3), porque parede de infraestrutura
    /// costuma ser transitória e merece mais paciência que enunciado errado; e bem abaixo das
    /// quatorze que a operação gastou contra a mesma parede em 03/08/2026.
    /// </summary>
    public int CardNoProgressCeiling { get; init; } = 5;

    /// <summary>
    /// Minutos sem NENHUMA entrega, com trabalho esperando, a partir dos quais a Bruna avisa o
    /// dono que a esteira parou. Vinte por padrão: uma tarefa real leva de três a dez minutos,
    /// então abaixo disso silêncio ainda é trabalho.
    /// </summary>
    public int DeliveryStallMinutes { get; init; } = 20;
}

/// <summary>Situação de um run de agente. Conjunto fechado.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentRunStatus>))]
public enum AgentRunStatus
{
    /// <summary>Claims, conta e worktree adquiridos; executor ainda não iniciado.</summary>
    Accepted,

    Running,
    Completed,
    Failed,
    Cancelled,

    /// <summary>Claim incompatível com outra tentativa viva.</summary>
    ScopeConflict,

    /// <summary>Recusado por política: papel, escopo, conta ou configuração.</summary>
    Rejected,
}

/// <summary>Pedido de bootstrap de um run de agente.</summary>
public sealed record StartAgentRunCommand
{
    public required string TenantId { get; init; }

    public required string ProjectId { get; init; }

    public required string TaskId { get; init; }

    public required string AttemptId { get; init; }

    /// <summary>Papel LÓGICO. Define o escopo de paths, nunca o provider.</summary>
    public required string Role { get; init; }

    /// <summary>Alias da conta. Nunca um e-mail, nunca uma credencial.</summary>
    public required string AccountAlias { get; init; }

    /// <summary>
    /// Este run é REFORÇO do chefe: a frota de especialistas do papel estava indisponível e o
    /// chefe emprestou a própria assinatura como executor extra. Só isto muda — a conta segue
    /// passando por adapter, cota, cooldown, autenticação e escopo de path como qualquer outra;
    /// o papel emprestado nunca é `critic` (ator ≠ crítico é invariante).
    /// </summary>
    public bool ChiefReinforcement { get; init; }

    public required string Instruction { get; init; }

    public required string RepositoryRoot { get; init; }

    public required string ControlledRoot { get; init; }

    public required string BranchName { get; init; }

    public required string WorktreePath { get; init; }

    public required IReadOnlyList<string> ScopeClaims { get; init; }

    public required string Owner { get; init; }

    public required string IdempotencyKey { get; init; }

    public AgentPathScopeKind PathScopeKind { get; init; } = AgentPathScopeKind.Backend;

    public ExternalAgentAccess Access { get; init; } = ExternalAgentAccess.Workspace;

    public string BaseReference { get; init; } = "HEAD";

    public string? Model { get; init; }

    public string? Effort { get; init; }

    public string? ResumeSessionId { get; init; }

    public string RiskTier { get; init; } = "medium";

    /// <summary>
    /// Workflow que governa este trabalho (a chave do template, ex. <c>playbook-standard</c>).
    /// Alimenta a seleção de contexto: sem ele o bundle era pedido para um workflow literal
    /// <c>agent-run</c>, que nenhum documento do manifesto declara.
    /// </summary>
    public string? WorkflowKey { get; init; }

    /// <summary>Fase real da esteira em que o card vive (ex. <c>5-Desenvolvimento</c>).</summary>
    public string? PhaseName { get; init; }

    /// <summary>
    /// Tipo real do card (<c>historia</c>, <c>tarefa</c>, <c>documento</c>, …). NÃO é o papel:
    /// o papel responde quem executa, o tipo responde que trabalho é.
    /// </summary>
    public string? CardType { get; init; }

    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = [];

    /// <summary>
    /// Ferramentas declaradas pela persona que executará este trabalho. O orquestrador
    /// resolve cada id no catálogo imediatamente antes de adquirir recursos e recusa o run
    /// quando qualquer item está ausente ou desabilitado.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> significa que o produtor não resolveu uma persona e deve ser
    /// recusado em fail-closed. Uma coleção vazia é diferente: a persona foi resolvida e declarou
    /// explicitamente não precisar de ferramentas.
    /// </remarks>
    public IReadOnlyList<string>? RequiredToolIds { get; init; }

    /// <summary>
    /// Chave da persona que o despachante resolveu para este card (Fase 2A.3). O orquestrador a
    /// usa para carregar a definição e injetá-la no bundle — sem isso o agente executa com papel e
    /// escopo, mas sem nenhuma palavra sobre COMO aquela especialidade pensa e onde ela para.
    /// </summary>
    public string? PersonaKey { get; init; }

    /// <summary>
    /// Contexto de continuação governada, quando esta tentativa retoma o trabalho de uma
    /// tentativa anterior reprovada. Nulo para um run do zero.
    /// </summary>
    public ContinuationContext? Continuation { get; init; }
}

/// <summary>
/// Estado observável de um run. Os campos duráveis vêm de `attempt_workspaces`; os campos
/// do executor vêm da sessão viva e do resultado coletado.
/// </summary>
public sealed record AgentRunSnapshot(
    string RunId,
    string AttemptId,
    string AccountAlias,
    string Role,
    string ExecutorId,
    AgentRunStatus Status,
    AttemptWorkspaceSnapshot? Workspace,
    IReadOnlyList<AttemptScopeConflict> Conflicts,
    ExternalAgentRunResult? Execution,
    string? SessionId,
    int? ProcessId,
    long? AccountFencingToken,
    string? BundleChecksum,
    string? ReceiptTurnId,
    string? FinalError);

/// <summary>Diagnóstico de uma conta: perfil em disco + executor observado por probe.</summary>
public sealed record AgentAccountDoctorReport(
    string Alias,
    string ExecutorId,
    bool AdapterImplemented,
    bool ExecutorInstalled,
    string? DetectedVersion,
    string ProbeReasonCode,
    AccountProfileDoctorReport Profile,
    bool Authenticated);

/// <summary>Pedido de revisão independente de uma tentativa (CA-7).</summary>
public sealed record AgentCriticReviewCommand
{
    public required string AttemptId { get; init; }

    /// <summary>Identidade durável opcional para contabilizar custo/capacidade do review.</summary>
    public string? TenantId { get; init; }

    public string? ProjectId { get; init; }

    public string? TaskId { get; init; }

    /// <summary>Conta do revisor. Precisa ser diferente da conta do actor.</summary>
    public required string CriticAlias { get; init; }

    public required string ActorAlias { get; init; }

    /// <summary>Diretório somente-leitura de onde o critic lê o repositório.</summary>
    public required string ReviewDirectory { get; init; }

    public required string Diff { get; init; }

    /// <summary>
    /// Pacote versionado que autorizou a tentativa. O crítico precisa comparar o resultado com
    /// fatos, proveniência, escopo, DoD e evidências exigidas — o diff isolado não contém a razão
    /// pela qual o trabalho existe. Conteúdo é dado não confiável, nunca instrução ao revisor.
    /// </summary>
    public string DelegationInstruction { get; init; } = string.Empty;

    public string TestEvidence { get; init; } = "(nenhuma evidência de teste foi fornecida)";

    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = [];

    public IReadOnlyList<string> ScopeClaims { get; init; } = [];

    public string? Model { get; init; }
}
