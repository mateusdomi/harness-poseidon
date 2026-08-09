import type {
  EvaluationRecommendationsResponse,
  LedgerReconciliation,
  MemorySearchResponse,
  MergeContention,
  Account,
  Agent,
  AgentDefinition,
  AnalyzeSolicitationInput,
  AppendTaskInstructionInput,
  ActivateLicenseInput,
  BackupHandle,
  ChatTurnHandle,
  ClassifyDocumentInput,
  CreatableResource,
  CreateAccountInput,
  CreateAgentDefinitionInput,
  DuplicateAgentDefinitionInput,
  CreateInputMap,
  CreateWorkflowTemplateInput,
  Diagnostics,
  DocumentVersion,
  DrainChiefTasksInput,
  HandoffChiefInput,
  License,
  LinkWorkflowTemplateInput,
  ListQuery,
  Model,
  MoveTaskInput,
  Page,
  PhaseObligationProgress,
  Profile,
  Project,
  ProjectReliability,
  PublishWorkflowDraftInput,
  PublishWorkflowVersionInput,
  RemovableResource,
  ResolveApprovalInput,
  ResourceKind,
  ResourceMap,
  RunTarget,
  SaveDocumentVersionInput,
  SetOperationModeInput,
  SetTaskPriorityInput,
  SolicitationAnalysis,
  StartChatTurnInput,
  Task,
  TaskInstruction,
  TransitionDocumentInput,
  TransitionSolicitationInput,
  Ulid,
  UpdatableResource,
  UpdateAccountInput,
  UpdateAgentDefinitionInput,
  UpdateInputMap,
  Workflow,
  WorkflowDraftInput,
  WorkflowTemplate,
  WorkflowVersion,
  Approval,
  Document,
  DesignSystemBundle,
  AttemptContext,
  PrototypingStage,
  Solicitation,
  AgentExecutor,
  EvaluationResult,
  FreshContextEvaluationInput,
  GovernanceMetric,
  GovernanceReceipt,
  ProjectReadinessSnapshot,
  GovernanceReceiptQuery,
  HashlinePatchInput,
  HashlinePatchResult,
  PatchBenchmark,
  StaleDocumentFinding,
  LearningCandidate,
  LearningCandidateComparison,
  LearningCandidateHistoryRecord,
  LearningCandidateMetrics,
  LearningCandidatePage,
  LearningCandidateQuery,
  LearningDecisionInput,
  LearningEvaluationInput,
  LearningEvidenceRecord,
  LearningShadowInput,
  LearningTransition,
  LearningTransitionInput,
  GovernanceDocTree,
  GovernanceDocContent,
  AgentAccountRoster,
  V3AccountAuthInstruction,
  V3ChiefAssignment,
  V3AuthorizeBuildInput,
  V3BuildMission,
  V3ProjectContext,
  V3UnderstandAnalyzeInput,
  ChannelLink,
  ChannelMessagePage,
  CreateChannelLinkInput,
} from '../contracts';

/**
 * Cliente REST da API Poseidon (base `/api/v1`).
 * CRUD genérico tipado pelo {@link ResourceMap} + comandos de domínio.
 * Trocar mock ↔ http não exige mexer em nenhum componente: a UI só vê esta interface.
 *
 * Imutabilidade é contrato: tarefas, solicitações, demandas, instruções e
 * versões NÃO têm update — correção cria nova versão/solicitação e estado
 * muda via comandos (moveTask, transitionSolicitation, ...).
 */
export interface ApiClient {
  list<K extends ResourceKind>(resource: K, query?: ListQuery): Promise<Page<ResourceMap[K]>>;
  get<K extends ResourceKind>(resource: K, id: Ulid): Promise<ResourceMap[K]>;
  create<K extends CreatableResource>(
    resource: K,
    input: CreateInputMap[K],
  ): Promise<ResourceMap[K]>;
  update<K extends UpdatableResource>(
    resource: K,
    id: Ulid,
    input: UpdateInputMap[K],
  ): Promise<ResourceMap[K]>;
  remove(resource: RemovableResource, id: Ulid): Promise<void>;

  /** Perfil da sessão local (modo pessoal; cookie de sessão). */
  getCurrentProfile(): Promise<Profile>;

  /** Última conversa aberta pelo perfil corrente naquele projeto, persistida no servidor. */
  recallActiveConversation(projectId: Ulid): Promise<Ulid | null>;
  /** Atualiza a conversa a restaurar quando o perfil voltar ao projeto. */
  rememberActiveConversation(projectId: Ulid, conversationId: Ulid): Promise<void>;

  /** Armazena uma logo PNG/JPEG no data dir gerenciado e atualiza a marca do projeto. */
  uploadProjectLogo(projectId: Ulid, file: File): Promise<Project>;
  /** Estado explicável da etapa opcional de Prototipação. */
  /** Auditoria por card: o que exatamente a tentativa recebeu de contexto. */
  getAttemptContext(attemptId: Ulid): Promise<AttemptContext>;
  getPrototypingStage(projectId: Ulid): Promise<PrototypingStage>;
  /** Valida e armazena um ZIP React como design system do projeto. */
  uploadDesignSystemBundle(
    projectId: Ulid,
    file: File,
    title?: string,
  ): Promise<DesignSystemBundle>;
  /** Persiste e processa a fonte; selecionar o arquivo localmente não conta como upload. */
  uploadSolicitationAttachment(solicitationId: Ulid, file: File): Promise<void>;

  /** Move tarefa entre colunas do quadro → emite `task.stateChanged`. */
  moveTask(taskId: Ulid, input: MoveTaskInput): Promise<Task>;
  /** Altera a prioridade da tarefa (ação humana; conteúdo permanece imutável). */
  setTaskPriority(taskId: Ulid, input: SetTaskPriorityInput): Promise<Task>;
  /**
   * Arquiva a tarefa (metaestado — não muda `state`). Permitido apenas para
   * tarefas concluídas (`done`); desarquivar é sempre permitido.
   */
  archiveTask(taskId: Ulid): Promise<Task>;
  /** Desarquiva a tarefa (remove `archivedAt`; `state` permanece). */
  unarchiveTask(taskId: Ulid): Promise<Task>;
  /** Nova versão de instrução (correção) — incrementa `instructionVersion`. */
  appendTaskInstruction(taskId: Ulid, input: AppendTaskInstructionInput): Promise<TaskInstruction>;
  /** Triagem de solicitação (apenas estado; conteúdo imutável). */
  transitionSolicitation(id: Ulid, input: TransitionSolicitationInput): Promise<Solicitation>;
  /** Resolve aprovação → emite `approval.resolved`. Reprovação exige `note`. */
  resolveApproval(id: Ulid, input: ResolveApprovalInput): Promise<Approval>;
  /** Transição da máquina de estados de documento → emite `document.stateChanged`. */
  transitionDocument(id: Ulid, input: TransitionDocumentInput): Promise<Document>;
  /**
   * Nova versão do documento por edição manual (origem humana — a versão
   * nasce com `authorKind: 'user'` e `version = currentVersion + 1`;
   * versões anteriores permanecem imutáveis).
   */
  saveDocumentVersion(id: Ulid, input: SaveDocumentVersionInput): Promise<DocumentVersion>;
  /**
   * Classificação de documento (metadados: rótulos + vínculo de fase) —
   * também usada para adotar documentos órfãos. Não emite evento próprio.
   */
  classifyDocument(id: Ulid, input: ClassifyDocumentInput): Promise<Document>;
  /**
   * Publica nova versão de template (imutável; número = última + 1) →
   * emite `workflow.versionPublished`. O template passa a apontar para ela.
   */
  publishWorkflowVersion(
    templateId: Ulid,
    input: PublishWorkflowVersionInput,
  ): Promise<WorkflowVersion>;

  /* ---- gestão de templates de workflow (FR-4) ---- */

  /** Cria template do zero — nasce rascunho, sem versão publicada. */
  createWorkflowTemplate(input: CreateWorkflowTemplateInput): Promise<WorkflowTemplate>;
  /**
   * Cria versão em RASCUNHO de um template (cópia da versão vigente quando
   * `input` é omitido). Rascunhos são editáveis até publicar.
   */
  createWorkflowDraftVersion(
    templateId: Ulid,
    input?: WorkflowDraftInput,
  ): Promise<WorkflowVersion>;
  /** Edita uma versão em rascunho (409 se não for rascunho). */
  updateWorkflowDraftVersion(versionId: Ulid, input: WorkflowDraftInput): Promise<WorkflowVersion>;
  /**
   * Publica um rascunho: validação do Harness (zod + regras) bloqueia com
   * 422 se inválido; ao publicar, congela (imutável) e o template passa a
   * apontar para a nova versão → emite `workflow.versionPublished`.
   */
  publishWorkflowDraft(
    versionId: Ulid,
    input?: PublishWorkflowDraftInput,
  ): Promise<WorkflowVersion>;
  /**
   * Arquiva (tombstone) template ou versão — NUNCA exclusão física.
   * A versão vigente do template não pode ser arquivada (409).
   */
  archiveWorkflowTemplate(templateId: Ulid): Promise<WorkflowTemplate>;
  archiveWorkflowVersion(versionId: Ulid): Promise<WorkflowVersion>;
  /**
   * Exclui APENAS rascunho nunca utilizado (sem vínculo de workflow/run) —
   * 409 caso contrário. Versão publicada/utilizada não pode ser excluída.
   */
  deleteWorkflowDraftVersion(versionId: Ulid): Promise<void>;
  /** Exclui template rascunho nunca utilizado (sem versões publicadas/vínculos) — 409 caso contrário. */
  deleteWorkflowTemplate(templateId: Ulid): Promise<void>;
  /** Duplica o template (nova cópia rascunho, com rascunho da versão vigente). */
  duplicateWorkflowTemplate(templateId: Ulid): Promise<WorkflowTemplate>;
  /** Duplica uma versão (publicada ou rascunho) como NOVO rascunho no mesmo template. */
  duplicateWorkflowVersion(versionId: Ulid): Promise<WorkflowVersion>;
  /**
   * Vincula template ao projeto: cria o `Workflow` com a versão publicada
   * vigente (ou a informada) — 409 se o projeto já tem workflow ou o
   * template não tem versão publicada.
   */
  linkWorkflowTemplate(input: LinkWorkflowTemplateInput): Promise<Workflow>;
  /**
   * Troca modo de operação do workflow com aceite de risco registrado →
   * emite `audit.eventAppended`. No semiautônomo define quais gates pausam.
   */
  setWorkflowOperationMode(workflowId: Ulid, input: SetOperationModeInput): Promise<Workflow>;

  /** Marca notificações como lidas; retorna quantas mudaram. */
  markNotificationsRead(ids: Ulid[]): Promise<number>;
  /** Silencia notificações; retorna quantas mudaram. */
  muteNotifications(ids: Ulid[]): Promise<number>;

  /**
   * Inicia turno do chefe numa conversa. A resposta chega via realtime:
   * `chat.turnStarted` → `chat.turnChunk`* → `chat.turnCompleted` + `message.appended`.
   */
  startChatTurn(conversationId: Ulid, input: StartChatTurnInput): Promise<ChatTurnHandle>;

  /**
   * Pausa a orquestração do chefe (projeto → `paused`, chefe → `waiting`)
   * → emite `agent.statusChanged` + `audit.eventAppended`.
   */
  pauseChief(projectId: Ulid): Promise<Project>;
  /** Retoma a orquestração (projeto → `active`, chefe → `idle`). */
  resumeChief(projectId: Ulid): Promise<Project>;
  /**
   * Passagem de bastão: nova instância assume a orquestração (opcionalmente
   * com outra definição/modelo), fencing token incrementado → emite
   * `agent.statusChanged` (antigo e novo) + `audit.eventAppended`.
   */
  handoffChief(projectId: Ulid, input: HandoffChiefInput): Promise<Agent>;
  /**
   * Drena as tarefas em andamento do projeto (volta para `ready`,
   * attempts running → `cancelled`, agentes → `idle`). Retorna quantas
   * tarefas foram drenadas.
   */
  drainChiefTasks(projectId: Ulid, input: DrainChiefTasksInput): Promise<number>;

  /* ---- rodar projeto (run-targets) ---- */

  /** Inicia um serviço detectado → `running`; logs chegam via `run.logAppended`. */
  startRunTarget(runTargetId: Ulid): Promise<RunTarget>;
  /** Para um serviço → `stopped`. */
  stopRunTarget(runTargetId: Ulid): Promise<RunTarget>;
  /** Reinicia um serviço (stop + start, com logs das duas fases). */
  restartRunTarget(runTargetId: Ulid): Promise<RunTarget>;
  /**
   * Cleanup do ambiente do projeto: para todos os serviços e registra
   * auditoria. Retorna quantos serviços foram parados.
   */
  cleanupRunEnvironment(projectId: Ulid): Promise<number>;

  /* ---- providers ---- */

  /**
   * Sincroniza o catálogo de modelos do provider (somente leitura na UI)
   * → emite `quota.updated` das contas do provider no stream global.
   */
  syncProviderCatalog(providerId: Ulid): Promise<Model[]>;

  /* ---- contas de provider (FR-5) ---- */

  /** Cria uma conta com referência write-only e metadados — nasce `disabled`. */
  createAccount(input: CreateAccountInput): Promise<Account>;
  /** Edita estado, apelido, identidade, plano, saúde, cota e capacidades. */
  updateAccount(id: Ulid, input: UpdateAccountInput): Promise<Account>;
  /** Habilita a conta (`state: active`). */
  enableAccount(id: Ulid): Promise<Account>;
  /** Desabilita a conta (`state: disabled`). */
  disableAccount(id: Ulid): Promise<Account>;
  /**
   * Remove somente conta desabilitada — 409 se estiver ativa ou se houver
   * budget/definição referenciando-a (remova as referências antes).
   */
  deleteAccount(id: Ulid): Promise<void>;

  /* ---- definições de agente (FR-5) ---- */

  /** Cria definição de agente — nasce `enabled`, `version: 1`. */
  createAgentDefinition(input: CreateAgentDefinitionInput): Promise<AgentDefinition>;
  /** Edita a definição — incrementa `version` e registra no histórico. */
  updateAgentDefinition(id: Ulid, input: UpdateAgentDefinitionInput): Promise<AgentDefinition>;
  /** Duplica a definição (cópia habilitada, sem instâncias, `version: 1`). */
  duplicateAgentDefinition(
    id: Ulid,
    input?: DuplicateAgentDefinitionInput,
  ): Promise<AgentDefinition>;
  /** Habilita a definição (`state: enabled`). */
  enableAgentDefinition(id: Ulid): Promise<AgentDefinition>;
  /** Desabilita a definição (`state: disabled`). */
  disableAgentDefinition(id: Ulid): Promise<AgentDefinition>;
  /**
   * Arquiva a definição (`state: archived` — tombstone). Instâncias
   * existentes continuam referenciando-a (histórico preservado).
   */
  archiveAgentDefinition(id: Ulid): Promise<AgentDefinition>;
  /**
   * Exclui SOMENTE definição nunca utilizada (sem instâncias de agente)
   * — 409 caso contrário (usar archiveAgentDefinition).
   */
  deleteAgentDefinition(id: Ulid): Promise<void>;

  /* ---- PO Assistant ---- */

  /**
   * Analisa uma solicitação em texto livre: cria a solicitação (kind
   * `request`) e devolve os painéis (requisitos, ambiguidades,
   * contradições, perguntas, critérios de aceite) para curadoria humana.
   */
  analyzeSolicitation(input: AnalyzeSolicitationInput): Promise<SolicitationAnalysis>;

  /* ---- licença, backup, diagnóstico ---- */

  /** Ativa a licença do dispositivo por chave → `audit.eventAppended`. */
  activateLicense(input: ActivateLicenseInput): Promise<License>;
  /** Cria um backup local → `audit.eventAppended`. */
  createBackup(): Promise<BackupHandle>;
  /** Restaura um backup → `audit.eventAppended`. */
  restoreBackup(backupId: Ulid): Promise<void>;
  /** Diagnóstico da instalação: versões, saúde e conexões. */
  getDiagnostics(): Promise<Diagnostics>;

  /* ---- prontidão do golden path (read model canônico, ADR-017) ---- */

  /**
   * Prontidão do projeto: etapas, bloqueadores e próximas ações.
   * FONTE DA VERDADE da jornada depois que o projeto existe — a UI apresenta,
   * não recalcula. 404 quando o projeto não existe.
   */
  getProjectReadiness(projectId: Ulid): Promise<ProjectReadinessSnapshot>;

  /**
   * Progresso aceito de uma fase derivado de `phase_obligations`.
   * Trabalho em voo e aprovação humana são retornados em campos separados.
   */
  getPhaseObligationProgress(runId: Ulid, phaseKey: string): Promise<PhaseObligationProgress>;

  /* ---- governança de agentes P1 ---- */

  listGovernanceReceipts(query?: GovernanceReceiptQuery): Promise<GovernanceReceipt[]>;
  getGovernanceReceipt(turnId: string): Promise<GovernanceReceipt>;
  listGovernanceMetrics(turnId: string): Promise<GovernanceMetric[]>;
  createFreshContextEvaluation(input: FreshContextEvaluationInput): Promise<EvaluationResult>;
  listStaleDocumentFindings(): Promise<StaleDocumentFinding[]>;
  applyHashlinePatch(projectId: string, input: HashlinePatchInput): Promise<HashlinePatchResult>;
  listHashlineBenchmark(): Promise<PatchBenchmark[]>;
  listAgentExecutors(): Promise<AgentExecutor[]>;

  /* ---- operação do runtime (fases 5/6/10/12) ---- */

  listEvaluationRecommendations(
    projectId: string,
    minSampleSize?: number,
  ): Promise<EvaluationRecommendationsResponse>;
  getProjectReliability(projectId: string, k?: number): Promise<ProjectReliability>;
  getMergeContention(): Promise<MergeContention>;
  reconcileLedger(): Promise<LedgerReconciliation>;
  searchMemory(query: string, projectId?: string, topK?: number): Promise<MemorySearchResponse>;

  /* ---- governança de aprendizado P2 ---- */

  listLearningCandidates(query?: LearningCandidateQuery): Promise<LearningCandidatePage>;
  getLearningCandidate(candidateId: string): Promise<LearningCandidate>;
  listLearningCandidateEvidence(candidateId: string): Promise<LearningEvidenceRecord[]>;
  compareLearningCandidate(candidateId: string): Promise<LearningCandidateComparison>;
  listLearningCandidateHistory(candidateId: string): Promise<LearningCandidateHistoryRecord[]>;
  getLearningCandidateMetrics(
    query?: Pick<LearningCandidateQuery, 'organizationId' | 'projectId'>,
  ): Promise<LearningCandidateMetrics>;
  transitionLearningCandidate(
    candidateId: string,
    transition: LearningTransition,
    input: LearningTransitionInput,
  ): Promise<LearningCandidate>;
  evaluateLearningCandidate(
    candidateId: string,
    input: LearningEvaluationInput,
  ): Promise<LearningCandidate>;
  shadowLearningCandidate(
    candidateId: string,
    input: LearningShadowInput,
  ): Promise<LearningCandidate>;
  decideLearningCandidate(
    candidateId: string,
    input: LearningDecisionInput,
  ): Promise<LearningCandidate>;

  /* ---- documentos de governança em disco (working tree) ---- */

  /**
   * Árvore dos arquivos de governança/documentação em disco (allowlist
   * `governance/`, `docs/`). Editar/excluir passa a valer para o runtime/Chefe,
   * que lê a governança do disco.
   */
  listGovernanceDocs(): Promise<GovernanceDocTree>;
  /** Conteúdo de um documento de governança (path relativo dentro do allowlist). */
  readGovernanceDoc(path: string): Promise<GovernanceDocContent>;
  /** Salva (cria/sobrescreve) um documento de governança no disco. */
  saveGovernanceDoc(path: string, content: string): Promise<GovernanceDocContent>;
  /** Exclui um documento de governança do disco. */
  deleteGovernanceDoc(path: string): Promise<void>;
  /* ---- fleet de execução + canais externos ---- */

  /**
   * Roster REDIGIDO das identidades de execução do Chefe (as 7 contas de agent-run:
   * chief/worker × provider). Só alias/provider/executor/papéis/estado — NUNCA credencial
   * ou token. São as identidades de execução da fleet, distintas das personas/definições.
   */
  listAgentAccounts(): Promise<AgentAccountRoster[]>;
  prepareAgentAccountAuth(alias: string): Promise<V3AccountAuthInstruction>;
  setChiefPrimary(alias: string): Promise<V3ChiefAssignment>;
  getChiefAssignment(): Promise<V3ChiefAssignment>;
  getV3ProjectContext(projectId: Ulid): Promise<V3ProjectContext>;
  analyzeV3Project(projectId: Ulid, input?: V3UnderstandAnalyzeInput): Promise<V3ProjectContext>;
  authorizeV3Build(projectId: Ulid, input: V3AuthorizeBuildInput): Promise<V3ProjectContext>;
  compileV3BuildMission(projectId: Ulid): Promise<V3BuildMission>;
  listV3BuildMissions(projectId: Ulid): Promise<V3BuildMission[]>;

  /** Canais externos vinculados (ex.: Telegram) do tenant. */
  listChannelLinks(): Promise<ChannelLink[]>;

  /**
   * Vincula um canal externo (Telegram/Teams) a um projeto. Idempotente por
   * identidade: revincular a mesma identidade devolve o vínculo existente.
   */
  createChannelLink(input: CreateChannelLinkInput): Promise<ChannelLink>;

  /** Histórico de mensagens de um canal externo (cursor opaco `afterMessageId`). */
  listChannelMessages(
    linkId: Ulid,
    query?: { afterMessageId?: string; limit?: number },
  ): Promise<ChannelMessagePage>;
}
