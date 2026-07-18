import type {
  AppendTaskInstructionInput,
  ChatTurnHandle,
  ClassifyDocumentInput,
  CreatableResource,
  CreateInputMap,
  ListQuery,
  MoveTaskInput,
  Page,
  Profile,
  PublishWorkflowVersionInput,
  RemovableResource,
  ResolveApprovalInput,
  ResourceKind,
  ResourceMap,
  SetOperationModeInput,
  SetTaskPriorityInput,
  StartChatTurnInput,
  Task,
  TaskInstruction,
  TransitionDocumentInput,
  TransitionSolicitationInput,
  Ulid,
  UpdatableResource,
  UpdateInputMap,
  Workflow,
  WorkflowVersion,
  Approval,
  Document,
  Solicitation,
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

  /** Move tarefa entre colunas do quadro → emite `task.stateChanged`. */
  moveTask(taskId: Ulid, input: MoveTaskInput): Promise<Task>;
  /** Altera a prioridade da tarefa (ação humana; conteúdo permanece imutável). */
  setTaskPriority(taskId: Ulid, input: SetTaskPriorityInput): Promise<Task>;
  /** Nova versão de instrução (correção) — incrementa `instructionVersion`. */
  appendTaskInstruction(taskId: Ulid, input: AppendTaskInstructionInput): Promise<TaskInstruction>;
  /** Triagem de solicitação (apenas estado; conteúdo imutável). */
  transitionSolicitation(id: Ulid, input: TransitionSolicitationInput): Promise<Solicitation>;
  /** Resolve aprovação → emite `approval.resolved`. Reprovação exige `note`. */
  resolveApproval(id: Ulid, input: ResolveApprovalInput): Promise<Approval>;
  /** Transição da máquina de estados de documento → emite `document.stateChanged`. */
  transitionDocument(id: Ulid, input: TransitionDocumentInput): Promise<Document>;
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
}
