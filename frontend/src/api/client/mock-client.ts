import {
  ApiError,
  appendTaskInstructionInputSchema,
  moveTaskInputSchema,
  resolveApprovalInputSchema,
  setOperationModeInputSchema,
  startChatTurnInputSchema,
  transitionDocumentInputSchema,
  transitionSolicitationInputSchema,
  type Agent,
  type Approval,
  type AppendTaskInstructionInput,
  type Attempt,
  type ChatTurnHandle,
  type CreatableResource,
  type CreateInputMap,
  type Demand,
  type Document,
  type ListQuery,
  type Message,
  type MoveTaskInput,
  type Notification,
  type Page,
  type ProblemDetails,
  type Profile,
  type RemovableResource,
  type ResolveApprovalInput,
  type ResourceKind,
  type ResourceMap,
  type SetOperationModeInput,
  type Solicitation,
  type StartChatTurnInput,
  type Task,
  type TaskInstruction,
  type TransitionDocumentInput,
  type TransitionSolicitationInput,
  type Ulid,
  type UpdatableResource,
  type UpdateInputMap,
  type Workflow,
} from '../contracts';
import { streams } from '../contracts';
import type { ApiClient } from './api-client';
import type { FixtureData } from '../fixtures';
import type { MockRealtimeClient } from '../realtime';

export interface MockApiClientOptions {
  /** Latência simulada por chamada (ms). Padrão 100–600. */
  latency?: { min: number; max: number };
  /** PRNG injetável (latência/cenários) — determinismo em testes. */
  random?: () => number;
  /** Gerador de ULID para novas entidades. */
  nextId: () => Ulid;
  /** Relógio ISO-8601 injetável. */
  now?: () => string;
  /** Perfil da sessão local (autor de ações humanas). */
  currentProfileId: Ulid;
  /** Realtime mock para emitir eventos das mutações (sistema vivo). */
  realtime?: MockRealtimeClient;
  /** Liga heartbeat automático de attempts em andamento. Padrão false. */
  simulateHeartbeats?: boolean;
  /** Intervalo do heartbeat (ms). Padrão 5000. */
  heartbeatIntervalMs?: number;
  /** Taxa de falha aleatória (0–1) para cenários de erro. Padrão 0. */
  failureRate?: number;
  /** Delay entre chunks do fluxo de chat (ms). Padrão 120. */
  chatChunkDelayMs?: number;
}

type Store = { [K in ResourceKind]: Map<string, ResourceMap[K]> };

/** Converte as fixtures (arrays) no store em memória (Maps por id). */
export function createMockStore(fixtures: FixtureData): Store {
  const store = {} as Store;
  for (const [resource, items] of Object.entries(fixtures.data) as [
    ResourceKind,
    { id: string }[],
  ][]) {
    (store[resource] as Map<string, { id: string }>) = new Map(
      items.map((item) => [item.id, structuredClone(item)]),
    );
  }
  return store;
}

const CHAT_REPLY_CHUNKS = [
  'Entendi o contexto. ',
  'Vou quebrar isso em tarefas e delegar aos especialistas. ',
  'Assim que houver evidências nas tentativas, te atualizo por aqui.',
];

/**
 * Cliente de API em memória (modo `VITE_API_MODE=mock`).
 * - Store alimentado pelas fixtures; latência 100–600 ms; erros simuláveis
 *   por cenário (`queueError` / `failureRate`).
 * - Mutações emitem os eventos correspondentes no MockRealtimeClient —
 *   a UI mockada se comporta como sistema vivo, sem depender de refetch.
 */
export class MockApiClient implements ApiClient {
  readonly #store: Store;
  readonly #options: Required<Omit<MockApiClientOptions, 'realtime'>> & {
    realtime?: MockRealtimeClient;
  };
  readonly #errorQueue: ProblemDetails[] = [];

  constructor(fixtures: FixtureData, options: MockApiClientOptions) {
    this.#store = createMockStore(fixtures);
    this.#options = {
      latency: options.latency ?? { min: 100, max: 600 },
      random: options.random ?? Math.random,
      nextId: options.nextId,
      now: options.now ?? (() => new Date().toISOString()),
      currentProfileId: options.currentProfileId,
      realtime: options.realtime,
      simulateHeartbeats: options.simulateHeartbeats ?? false,
      heartbeatIntervalMs: options.heartbeatIntervalMs ?? 5_000,
      failureRate: options.failureRate ?? 0,
      chatChunkDelayMs: options.chatChunkDelayMs ?? 120,
    };
    if (this.#options.realtime && this.#options.simulateHeartbeats) {
      for (const attempt of this.#table('attempts').values()) {
        if (attempt.state === 'running') this.#startHeartbeat(attempt);
      }
    }
  }

  /** Cenário de erro: a próxima chamada falha com este problem+json. */
  queueError(problem: ProblemDetails): void {
    this.#errorQueue.push(problem);
  }

  /* ---- CRUD genérico ---- */

  async list<K extends ResourceKind>(resource: K, query?: ListQuery): Promise<Page<ResourceMap[K]>> {
    await this.#simulate();
    let items = [...this.#table(resource).values()];
    const filter = query?.filter ?? {};
    for (const [key, value] of Object.entries(filter)) {
      if (value === undefined) continue;
      items = items.filter((item) => (item as Record<string, unknown>)[key] === value);
    }
    const offset = query?.cursor ? Number.parseInt(query.cursor, 10) : 0;
    const limit = query?.limit ?? items.length;
    const pageItems = items.slice(offset, offset + limit);
    const nextOffset = offset + limit;
    return {
      items: pageItems.map((item) => structuredClone(item)),
      nextCursor: nextOffset < items.length ? String(nextOffset) : null,
    };
  }

  async get<K extends ResourceKind>(resource: K, id: Ulid): Promise<ResourceMap[K]> {
    await this.#simulate();
    const item = this.#table(resource).get(id);
    if (!item) throw this.#notFound(resource, id);
    return structuredClone(item);
  }

  async create<K extends CreatableResource>(
    resource: K,
    input: CreateInputMap[K],
  ): Promise<ResourceMap[K]> {
    await this.#simulate();
    const entity = this.#build(resource, input);
    (this.#table(resource) as Map<string, unknown>).set(entity.id, entity);
    this.#emitCreated(resource, entity as ResourceMap[K]);
    return structuredClone(entity) as ResourceMap[K];
  }

  async update<K extends UpdatableResource>(
    resource: K,
    id: Ulid,
    input: UpdateInputMap[K],
  ): Promise<ResourceMap[K]> {
    await this.#simulate();
    const table = this.#table(resource) as Map<string, Record<string, unknown>>;
    const current = table.get(id);
    if (!current) throw this.#notFound(resource, id);
    const next = { ...current, ...(input as Record<string, unknown>) };
    if ('updatedAt' in next) next.updatedAt = this.#options.now();
    table.set(id, next);
    return structuredClone(next) as ResourceMap[K];
  }

  async remove(resource: RemovableResource, id: Ulid): Promise<void> {
    await this.#simulate();
    if (!this.#table(resource).delete(id)) throw this.#notFound(resource, id);
  }

  async getCurrentProfile(): Promise<Profile> {
    await this.#simulate();
    const profile = this.#table('profiles').get(this.#options.currentProfileId);
    if (!profile) throw this.#notFound('profiles', this.#options.currentProfileId);
    return structuredClone(profile);
  }

  /* ---- comandos de domínio ---- */

  async moveTask(taskId: Ulid, input: MoveTaskInput): Promise<Task> {
    await this.#simulate();
    const parsed = moveTaskInputSchema.parse(input);
    const task = this.#require('tasks', taskId);
    const from = task.state;
    task.state = parsed.toState;
    task.updatedAt = this.#options.now();
    task.blockedReason = parsed.toState === 'blocked' ? (parsed.note ?? task.blockedReason) : null;
    this.#options.realtime?.emit(streams.project(task.projectId), 'task.stateChanged', {
      taskId: task.id,
      from,
      to: parsed.toState,
      changedByKind: 'user',
      note: parsed.note ?? null,
    });
    this.#options.realtime?.emit(streams.task(task.id), 'task.stateChanged', {
      taskId: task.id,
      from,
      to: parsed.toState,
      changedByKind: 'user',
      note: parsed.note ?? null,
    });
    return structuredClone(task);
  }

  async appendTaskInstruction(
    taskId: Ulid,
    input: AppendTaskInstructionInput,
  ): Promise<TaskInstruction> {
    await this.#simulate();
    const parsed = appendTaskInstructionInputSchema.parse(input);
    const task = this.#require('tasks', taskId);
    const instruction: TaskInstruction = {
      id: this.#options.nextId(),
      taskId: task.id,
      version: task.instructionVersion + 1,
      body: parsed.body,
      authorKind: 'user',
      authorId: this.#options.currentProfileId,
      createdAt: this.#options.now(),
    };
    this.#table('task-instructions').set(instruction.id, instruction);
    task.instructionVersion = instruction.version;
    task.updatedAt = this.#options.now();
    return structuredClone(instruction);
  }

  async transitionSolicitation(
    id: Ulid,
    input: TransitionSolicitationInput,
  ): Promise<Solicitation> {
    await this.#simulate();
    const parsed = transitionSolicitationInputSchema.parse(input);
    const solicitation = this.#require('solicitations', id);
    solicitation.state = parsed.state;
    return structuredClone(solicitation);
  }

  async resolveApproval(id: Ulid, input: ResolveApprovalInput): Promise<Approval> {
    await this.#simulate();
    let parsed: ResolveApprovalInput;
    try {
      parsed = resolveApprovalInputSchema.parse(input);
    } catch {
      throw ApiError.of(400, 'Entrada inválida', 'Reprovação exige observação (note).', {
        note: ['Observação obrigatória ao reprovar.'],
      });
    }
    const approval = this.#require('approvals', id);
    if (approval.state !== 'pending') {
      throw ApiError.of(409, 'Aprovação já resolvida', `Aprovação ${id} não está pendente.`);
    }
    approval.state = parsed.decision;
    approval.resolvedByProfileId = this.#options.currentProfileId;
    approval.resolvedAt = this.#options.now();
    approval.resolutionNote = parsed.note ?? null;

    if (approval.gateId) {
      const gate = this.#table('gates').get(approval.gateId);
      if (gate && gate.state === 'pending') {
        const gateFrom = gate.state;
        gate.state = parsed.decision;
        gate.decidedByProfileId = this.#options.currentProfileId;
        gate.decidedAt = approval.resolvedAt;
        gate.note = parsed.note ?? null;
        this.#options.realtime?.emit(streams.project(approval.projectId), 'gate.changed', {
          gateId: gate.id,
          runId: gate.runId,
          from: gateFrom,
          to: parsed.decision,
          decidedByProfileId: this.#options.currentProfileId,
          note: parsed.note ?? null,
        });
      }
    }

    this.#options.realtime?.emit(streams.project(approval.projectId), 'approval.resolved', {
      approvalId: approval.id,
      state: parsed.decision,
      resolvedByProfileId: this.#options.currentProfileId,
      note: parsed.note ?? null,
    });
    return structuredClone(approval);
  }

  async transitionDocument(id: Ulid, input: TransitionDocumentInput): Promise<Document> {
    await this.#simulate();
    const parsed = transitionDocumentInputSchema.parse(input);
    const document = this.#require('documents', id);
    const from = document.state;
    document.state = parsed.toState;
    document.updatedAt = this.#options.now();
    this.#options.realtime?.emit(streams.project(document.projectId), 'document.stateChanged', {
      documentId: document.id,
      from,
      to: parsed.toState,
    });
    return structuredClone(document);
  }

  async setWorkflowOperationMode(
    workflowId: Ulid,
    input: SetOperationModeInput,
  ): Promise<Workflow> {
    await this.#simulate();
    const parsed = setOperationModeInputSchema.parse(input);
    const workflow = this.#require('workflows', workflowId);
    workflow.operationMode = parsed.mode;
    workflow.semiautonomousPauseGates =
      parsed.mode === 'semiautonomous' ? (parsed.semiautonomousPauseGates ?? []) : [];
    workflow.riskAcceptances.push({
      mode: parsed.mode,
      acceptedByProfileId: this.#options.currentProfileId,
      note: parsed.riskAcceptanceNote,
      acceptedAt: this.#options.now(),
    });
    const auditEvent = {
      id: this.#options.nextId(),
      actorKind: 'user' as const,
      actorId: this.#options.currentProfileId,
      action: 'workflow.operationModeChanged',
      targetType: 'workflow',
      targetId: workflow.id,
      detail: `Modo alterado para ${parsed.mode}. Aceite: ${parsed.riskAcceptanceNote}`,
      occurredAt: this.#options.now(),
    };
    this.#table('audit-events').set(auditEvent.id, auditEvent);
    this.#options.realtime?.emit(streams.global(), 'audit.eventAppended', { auditEvent });
    return structuredClone(workflow);
  }

  async markNotificationsRead(ids: Ulid[]): Promise<number> {
    await this.#simulate();
    let count = 0;
    for (const id of ids) {
      const notification = this.#table('notifications').get(id);
      if (notification && notification.status === 'unread') {
        notification.status = 'read';
        notification.readAt = this.#options.now();
        count += 1;
      }
    }
    return count;
  }

  async muteNotifications(ids: Ulid[]): Promise<number> {
    await this.#simulate();
    let count = 0;
    for (const id of ids) {
      const notification = this.#table('notifications').get(id);
      if (notification && notification.status !== 'muted') {
        notification.status = 'muted';
        count += 1;
      }
    }
    return count;
  }

  async startChatTurn(conversationId: Ulid, input: StartChatTurnInput): Promise<ChatTurnHandle> {
    await this.#simulate();
    const parsed = startChatTurnInputSchema.parse(input);
    const conversation = this.#require('conversations', conversationId);
    const project = this.#require('projects', conversation.projectId);

    const userMessage = {
      id: this.#options.nextId(),
      conversationId: conversation.id,
      authorRole: 'user' as const,
      authorProfileId: this.#options.currentProfileId,
      authorAgentId: null,
      content: parsed.content,
      tokenCount: null,
      createdAt: this.#options.now(),
    };
    this.#table('messages').set(userMessage.id, userMessage);
    conversation.lastMessageAt = userMessage.createdAt;
    this.#options.realtime?.emit(streams.conversation(conversation.id), 'message.appended', {
      message: structuredClone(userMessage),
    });

    const turnId = this.#options.nextId();
    const replyMessage = {
      id: this.#options.nextId(),
      conversationId: conversation.id,
      authorRole: 'chief' as const,
      authorProfileId: null,
      authorAgentId: project.chiefAgentId,
      content: CHAT_REPLY_CHUNKS.join(''),
      tokenCount: 128,
      createdAt: this.#options.now(),
    };

    this.#options.realtime?.simulateChatTurn({
      conversationId: conversation.id,
      turnId,
      agentId: project.chiefAgentId,
      chunks: CHAT_REPLY_CHUNKS,
      messageId: replyMessage.id,
      chunkDelayMs: this.#options.chatChunkDelayMs,
      onCompleted: () => {
        this.#table('messages').set(replyMessage.id, replyMessage);
        conversation.lastMessageAt = replyMessage.createdAt;
        this.#options.realtime?.emit(streams.conversation(conversation.id), 'message.appended', {
          message: structuredClone(replyMessage),
        });
      },
    });

    return { turnId, conversationId: conversation.id };
  }

  /* ---- internos ---- */

  #table<K extends ResourceKind>(resource: K): Map<string, ResourceMap[K]> {
    return this.#store[resource];
  }

  #require<K extends ResourceKind>(resource: K, id: Ulid): ResourceMap[K] {
    const item = this.#table(resource).get(id);
    if (!item) throw this.#notFound(resource, id);
    return item;
  }

  #notFound(resource: ResourceKind, id: string): ApiError {
    return ApiError.of(404, 'Recurso não encontrado', `${resource}/${id} não existe.`);
  }

  /** Latência simulada + cenários de erro (fila explícita e taxa aleatória). */
  async #simulate(): Promise<void> {
    const { min, max } = this.#options.latency;
    const ms = min + this.#options.random() * (max - min);
    await new Promise((resolve) => setTimeout(resolve, ms));
    const queued = this.#errorQueue.shift();
    if (queued) throw new ApiError(queued);
    if (this.#options.failureRate > 0 && this.#options.random() < this.#options.failureRate) {
      throw ApiError.of(500, 'Falha simulada', 'Erro aleatório do cenário de teste (failureRate).');
    }
  }

  #startHeartbeat(attempt: Attempt): void {
    this.#options.realtime?.startAttemptHeartbeat({
      attemptId: attempt.id,
      taskId: attempt.taskId,
      intervalMs: this.#options.heartbeatIntervalMs,
    });
  }

  /** Constrói a entidade de cada recurso criável, com defaults do contrato. */
  #build<K extends CreatableResource>(resource: K, input: CreateInputMap[K]): ResourceMap[K] {
    const now = this.#options.now();
    const id = this.#options.nextId();
    switch (resource) {
      case 'tasks': {
        const i = input as CreateInputMap['tasks'];
        const instruction: TaskInstruction = {
          id: this.#options.nextId(),
          taskId: id,
          version: 1,
          body: i.instruction,
          authorKind: 'chief',
          authorId: null,
          createdAt: now,
        };
        this.#table('task-instructions').set(instruction.id, instruction);
        return ({
          id,
          projectId: i.projectId,
          demandId: i.demandId ?? null,
          title: i.title,
          state: 'backlog',
          priority: i.priority ?? 'medium',
          assigneeAgentId: i.assigneeAgentId ?? null,
          blockedReason: null,
          instructionVersion: 1,
          progress: { executed: 0, validated: 0, approved: 0 },
          createdAt: now,
          updatedAt: now,
          dueAt: i.dueAt ?? null,
        } satisfies Task) as unknown as ResourceMap[K];
      }
      case 'solicitations': {
        const i = input as CreateInputMap['solicitations'];
        return ({
          id,
          projectId: i.projectId,
          authorProfileId: this.#options.currentProfileId,
          kind: i.kind,
          title: i.title,
          body: i.body,
          state: 'open',
          supersedesId: i.supersedesId ?? null,
          createdAt: now,
        } satisfies Solicitation) as unknown as ResourceMap[K];
      }
      case 'messages': {
        const i = input as CreateInputMap['messages'];
        return {
          id,
          conversationId: i.conversationId,
          authorRole: 'user',
          authorProfileId: this.#options.currentProfileId,
          authorAgentId: null,
          content: i.content,
          tokenCount: null,
          createdAt: now,
        } as unknown as ResourceMap[K];
      }
      case 'notifications': {
        const i = input as CreateInputMap['notifications'];
        return ({
          id,
          profileId: i.profileId,
          severity: i.severity,
          category: i.category,
          title: i.title,
          body: i.body,
          groupKey: i.groupKey ?? null,
          dedupeCount: 1,
          status: 'unread',
          link: i.link ?? null,
          createdAt: now,
          readAt: null,
        } satisfies Notification) as unknown as ResourceMap[K];
      }
      case 'approvals': {
        const i = input as CreateInputMap['approvals'];
        return ({
          id,
          projectId: i.projectId,
          gateId: i.gateId ?? null,
          taskId: i.taskId ?? null,
          documentId: i.documentId ?? null,
          title: i.title,
          description: i.description,
          state: 'pending',
          requestedByAgentId: i.requestedByAgentId,
          requestedAt: now,
          resolvedByProfileId: null,
          resolvedAt: null,
          resolutionNote: null,
        } satisfies Approval) as unknown as ResourceMap[K];
      }
      case 'conversations': {
        const i = input as CreateInputMap['conversations'];
        return {
          id,
          projectId: i.projectId,
          title: i.title,
          state: 'active',
          createdByProfileId: this.#options.currentProfileId,
          createdAt: now,
          lastMessageAt: null,
        } as unknown as ResourceMap[K];
      }
      case 'demands': {
        const i = input as CreateInputMap['demands'];
        return {
          id,
          projectId: i.projectId,
          solicitationId: i.solicitationId ?? null,
          title: i.title,
          description: i.description,
          state: 'open',
          priority: i.priority ?? 'medium',
          createdAt: now,
        } as unknown as ResourceMap[K];
      }
      case 'documents': {
        const i = input as CreateInputMap['documents'];
        const version = {
          id: this.#options.nextId(),
          documentId: id,
          version: 1,
          body: i.body,
          authorKind: 'user' as const,
          authorId: this.#options.currentProfileId,
          createdAt: now,
        };
        this.#table('document-versions').set(version.id, version);
        return {
          id,
          projectId: i.projectId,
          title: i.title,
          kind: i.kind,
          state: 'planned',
          currentVersion: 1,
          classifications: [],
          inconsistent: false,
          waiver: null,
          createdAt: now,
          updatedAt: now,
        } as unknown as ResourceMap[K];
      }
      case 'document-versions': {
        const i = input as CreateInputMap['document-versions'];
        const document = this.#require('documents', i.documentId);
        document.currentVersion += 1;
        document.updatedAt = now;
        return {
          id,
          documentId: i.documentId,
          version: document.currentVersion,
          body: i.body,
          authorKind: 'user' as const,
          authorId: this.#options.currentProfileId,
          createdAt: now,
        } as unknown as ResourceMap[K];
      }
      case 'task-instructions': {
        const i = input as CreateInputMap['task-instructions'];
        const task = this.#require('tasks', i.taskId);
        task.instructionVersion += 1;
        task.updatedAt = now;
        return {
          id,
          taskId: i.taskId,
          version: task.instructionVersion,
          body: i.body,
          authorKind: 'user' as const,
          authorId: this.#options.currentProfileId,
          createdAt: now,
        } as unknown as ResourceMap[K];
      }
      case 'prototypes': {
        const i = input as CreateInputMap['prototypes'];
        return {
          id,
          projectId: i.projectId,
          name: i.name,
          description: i.description ?? '',
          state: 'draft',
          url: null,
          thumbnailUrl: null,
          sourceDocumentId: i.sourceDocumentId ?? null,
          createdAt: now,
          updatedAt: now,
        } as unknown as ResourceMap[K];
      }
      case 'visual-references': {
        const i = input as CreateInputMap['visual-references'];
        return {
          id,
          projectId: i.projectId,
          prototypeId: i.prototypeId ?? null,
          title: i.title,
          imageUrl: i.imageUrl,
          source: i.source,
          tags: i.tags ?? [],
          createdAt: now,
        } as unknown as ResourceMap[K];
      }
      case 'projects': {
        const i = input as CreateInputMap['projects'];
        return {
          id,
          organizationId: i.organizationId,
          name: i.name,
          key: i.key,
          description: i.description,
          repositoryUrl: i.repositoryUrl ?? null,
          chiefAgentId: id, // placeholder: backend vincula o chefe provisionado
          operationMode: 'manual',
          createdAt: now,
        } as unknown as ResourceMap[K];
      }
      default:
        throw ApiError.of(400, 'Recurso não criável', `Criação de ${resource} não suportada.`);
    }
  }

  /** Emite o evento de criação correspondente, se houver no catálogo. */
  #emitCreated(resource: CreatableResource, entity: ResourceMap[CreatableResource]): void {
    const realtime = this.#options.realtime;
    if (!realtime) return;
    switch (resource) {
      case 'tasks': {
        const task = entity as Task;
        realtime.emit(streams.project(task.projectId), 'task.created', { task });
        break;
      }
      case 'demands': {
        const demand = entity as Demand;
        realtime.emit(streams.project(demand.projectId), 'demand.created', { demand });
        break;
      }
      case 'messages': {
        const message = entity as Message;
        realtime.emit(streams.conversation(message.conversationId), 'message.appended', {
          message,
        });
        break;
      }
      case 'notifications': {
        const notification = entity as Notification;
        realtime.emit(streams.profile(notification.profileId), 'notification.created', {
          notification,
        });
        break;
      }
      case 'approvals': {
        const approval = entity as Approval;
        realtime.emit(streams.project(approval.projectId), 'approval.requested', { approval });
        break;
      }
      default:
        break;
    }
  }

  /** Heartbeat manual para tentativas iniciadas em runtime (uso futuro). */
  watchAttempt(attemptId: Ulid): void {
    const attempt = this.#table('attempts').get(attemptId);
    if (attempt && attempt.state === 'running') this.#startHeartbeat(attempt);
  }

  /** Agente alterado (uso futuro em cenários). */
  setAgentState(agentId: Ulid, state: Agent['state']): void {
    const agent = this.#table('agents').get(agentId);
    if (!agent) throw this.#notFound('agents', agentId);
    const from = agent.state;
    agent.state = state;
    this.#options.realtime?.emit(streams.global(), 'agent.statusChanged', {
      agentId,
      from,
      to: state,
      currentTaskId: agent.currentTaskId,
    });
  }
}
