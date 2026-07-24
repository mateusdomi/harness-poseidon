import {
  ApiError,
  activateLicenseInputSchema,
  analyzeSolicitationInputSchema,
  appendTaskInstructionInputSchema,
  classifyDocumentInputSchema,
  createAccountInputSchema,
  createAgentDefinitionInputSchema,
  createWorkflowTemplateInputSchema,
  drainChiefTasksInputSchema,
  handoffChiefInputSchema,
  linkWorkflowTemplateInputSchema,
  moveTaskInputSchema,
  publishWorkflowDraftInputSchema,
  publishWorkflowVersionInputSchema,
  resolveApprovalInputSchema,
  saveDocumentVersionInputSchema,
  setOperationModeInputSchema,
  setTaskPriorityInputSchema,
  startChatTurnInputSchema,
  transitionDocumentInputSchema,
  transitionSolicitationInputSchema,
  updateAccountInputSchema,
  updateAgentDefinitionInputSchema,
  validateWorkflowVersionContent,
  workflowDraftInputSchema,
  type Account,
  type Agent,
  type AgentDefinition,
  type Approval,
  type AnalyzeSolicitationInput,
  type AppendTaskInstructionInput,
  type ActivateLicenseInput,
  type Attempt,
  type BackupHandle,
  type ChatTurnHandle,
  type ClassifyDocumentInput,
  type CreatableResource,
  type CreateAccountInput,
  type CreateAgentDefinitionInput,
  type DuplicateAgentDefinitionInput,
  type CreateInputMap,
  type CreateWorkflowTemplateInput,
  type Demand,
  type Diagnostics,
  type Document,
  type DocumentVersion,
  type DrainChiefTasksInput,
  type HandoffChiefInput,
  type License,
  type LinkWorkflowTemplateInput,
  type ListQuery,
  type Message,
  type Model,
  type MoveTaskInput,
  type Notification,
  type Page,
  type ProblemDetails,
  type Profile,
  type Project,
  type Prototype,
  type PublishWorkflowDraftInput,
  type PublishWorkflowVersionInput,
  type RemovableResource,
  type ResolveApprovalInput,
  type ResourceKind,
  type ResourceMap,
  type RunTarget,
  type SaveDocumentVersionInput,
  type SetOperationModeInput,
  type SetTaskPriorityInput,
  type Solicitation,
  type SolicitationAnalysis,
  type StartChatTurnInput,
  type Task,
  type TaskInstruction,
  type TransitionDocumentInput,
  type TransitionSolicitationInput,
  type Ulid,
  type UpdatableResource,
  type UpdateAccountInput,
  type UpdateAgentDefinitionInput,
  type UpdateInputMap,
  type Workflow,
  type WorkflowDraftInput,
  type WorkflowTemplate,
  type WorkflowVersion,
  type AgentExecutor,
  type EvaluationResult,
  type GovernanceMetric,
  type GovernanceReceipt,
  type HashlinePatchResult,
  type PatchBenchmark,
  type StaleDocumentFinding,
  type LearningCandidate,
  type LearningCandidateComparison,
  type LearningCandidateHistoryRecord,
  type LearningCandidateMetrics,
  type LearningCandidatePage,
  type LearningEvidenceRecord,
  type GovernanceDocTree,
  type GovernanceDocContent,
  type AgentAccountRoster,
  type ChannelLink,
  type ChannelMessagePage,
  type CreateChannelLinkInput,
} from '../contracts';
import { streams } from '../contracts';
import { product } from '@/config/product';
import type { ApiClient } from './api-client';
import type { ProjectReadinessSnapshot } from '../contracts/readiness';
import type { FixtureData } from '../fixtures';
import { buildMockReadinessSnapshot } from '../fixtures/readiness';
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
 * Gatilho determinístico (mock/E2E): mensagem contendo `[plan]` faz o chefe
 * "planejar" de verdade — cria demanda + 2 tarefas, move uma tarefa pelas
 * colunas (eventos `task.stateChanged`) e abre uma aprovação de gate.
 * Títulos fixos para seletores estáveis nos testes E2E.
 */
export const CHIEF_PLAN_TRIGGER = /\[plan\]/i;
export const CHIEF_PLAN_TASK_A = 'Decompor escopo do plano';
export const CHIEF_PLAN_TASK_B = 'Executar primeira entrega do plano';
export const CHIEF_PLAN_APPROVAL_TITLE = 'Aprovar gate do plano simulado';

/**
 * Gatilhos determinísticos da análise do PO Assistant (mock/E2E):
 * - perguntas/critérios fixos (seletores estáveis nos testes);
 * - `[contradição]` no texto gera um item no painel de contradições.
 */
export const PO_ANALYSIS_QUESTION_DEADLINE = 'Qual é o prazo esperado para esta entrega?';
export const PO_ANALYSIS_QUESTION_USERS = 'Quem são os usuários impactados?';
export const PO_ANALYSIS_CRITERION_REVIEW =
  'Critérios de aceite revisados e aprovados pelo responsável do projeto.';
export const PO_ANALYSIS_CONTRADICTION_TRIGGER = /\[contradição\]/i;

/** Intervalo entre linhas de log simuladas de um run-target (ms). */
const RUN_LOG_STEP_MS = 400;

/**
 * Roster canônico de identidades de execução (ADR-021) — as 7 contas de agent-run da fleet.
 * REDIGIDO por construção: só alias/provider/executor/papéis/estado, nunca credencial.
 */
const MOCK_AGENT_ROSTER: readonly AgentAccountRoster[] = [
  { alias: 'chief-claude-primary', providerKind: 'anthropic', executorId: 'claude-code', roles: ['chief-orchestrator'], concurrencyLimit: 1, priority: 100, enabled: true, state: 'authentication-required' },
  { alias: 'worker-claude-secondary', providerKind: 'anthropic', executorId: 'claude-code', roles: ['backend-specialist'], concurrencyLimit: 1, priority: 100, enabled: true, state: 'authentication-required' },
  { alias: 'worker-codex-frontend', providerKind: 'openai', executorId: 'codex', roles: ['frontend-specialist'], concurrencyLimit: 1, priority: 100, enabled: true, state: 'authentication-required' },
  { alias: 'worker-codex-critic', providerKind: 'openai', executorId: 'codex', roles: ['critic'], concurrencyLimit: 1, priority: 80, enabled: true, state: 'authentication-required' },
  { alias: 'worker-antigravity-review', providerKind: 'antigravity', executorId: 'antigravity', roles: ['critic'], concurrencyLimit: 1, priority: 90, enabled: true, state: 'authentication-required' },
  { alias: 'worker-glm-general', providerKind: 'zhipu', executorId: 'glm', roles: ['backend-specialist'], concurrencyLimit: 1, priority: 60, enabled: true, state: 'authentication-required' },
  { alias: 'worker-kimi-ui', providerKind: 'moonshot', executorId: 'kimi-code', roles: ['frontend-specialist'], concurrencyLimit: 1, priority: 70, enabled: true, state: 'authentication-required' },
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
  /** Vínculos de canal criados via UI (mock in-memory, além do Telegram semente). */
  readonly #channelLinks: ChannelLink[] = [];
  readonly #governanceDocs = new Map<string, { content: string; modifiedAt: string }>([
    ['governance/core.md', { content: '# Núcleo da governança\n\nRegras canônicas do sistema.\n', modifiedAt: '2026-07-01T09:00:00Z' }],
    ['governance/rules/frontend.md', { content: '# Regras de frontend\n\nGates verdes obrigatórios.\n', modifiedAt: '2026-07-05T10:00:00Z' }],
    ['governance/manifest.yaml', { content: 'version: 1\ndocuments: []\n', modifiedAt: '2026-07-02T11:00:00Z' }],
    ['docs/INDEX.md', { content: '# Índice da documentação\n\nMapa dos documentos.\n', modifiedAt: '2026-07-03T12:00:00Z' }],
  ]);

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
      // Parâmetro de consulta do endpoint V3, não campo da entidade.
      if (resource === 'agent-definitions' && key === 'includeArchived') {
        if (value !== true) {
          items = items.filter(
            (item) =>
              (item as AgentDefinition).state !== 'archived' &&
              !(item as AgentDefinition).archivedAt,
          );
        }
        continue;
      }
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
    if (resource === 'projects') {
      // Campos versionados (repositório, tecnologias, marca) incrementam
      // configVersion e registram entrada no histórico — APENAS quando o
      // valor muda de fato (FR-4: edição de metadados não gera versão).
      const versioned = [
        'repositoryUrl',
        'repositoryProvider',
        'defaultBranch',
        'technologies',
        'brand',
      ] as const;
      const inputRecord = input as Record<string, unknown>;
      const changedFields = versioned.filter(
        (field) =>
          field in inputRecord &&
          JSON.stringify(inputRecord[field]) !== JSON.stringify(current[field]),
      );
      if (changedFields.length > 0) {
        next.configVersion = ((current.configVersion as number) ?? 0) + 1;
        next.configHistory = [
          ...((current.configHistory as Project['configHistory']) ?? []),
          {
            version: next.configVersion as number,
            changedAt: this.#options.now(),
            changedFields: [...changedFields],
            summary: `Campos alterados: ${changedFields.join(', ')}.`,
          },
        ];
      }
      next.lastActivityAt = this.#options.now();
    }
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

  async setTaskPriority(taskId: Ulid, input: SetTaskPriorityInput): Promise<Task> {
    await this.#simulate();
    const parsed = setTaskPriorityInputSchema.parse(input);
    const task = this.#require('tasks', taskId);
    task.priority = parsed.priority;
    task.updatedAt = this.#options.now();
    return structuredClone(task);
  }

  async archiveTask(taskId: Ulid): Promise<Task> {
    await this.#simulate();
    const task = this.#require('tasks', taskId);
    if (task.archivedAt !== null) {
      throw ApiError.of(409, 'Tarefa já arquivada', `Tarefa ${taskId} já está arquivada.`);
    }
    if (task.state !== 'done') {
      throw ApiError.of(
        409,
        'Arquivamento não permitido',
        'Apenas tarefas concluídas podem ser arquivadas.',
      );
    }
    task.archivedAt = this.#options.now();
    task.updatedAt = this.#options.now();
    return structuredClone(task);
  }

  async unarchiveTask(taskId: Ulid): Promise<Task> {
    await this.#simulate();
    const task = this.#require('tasks', taskId);
    if (task.archivedAt === null) {
      throw ApiError.of(409, 'Tarefa não arquivada', `Tarefa ${taskId} não está arquivada.`);
    }
    task.archivedAt = null;
    task.updatedAt = this.#options.now();
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

    if (approval.documentId) {
      const document = this.#table('documents').get(approval.documentId);
      if (document && document.state === 'awaitingApproval') {
        const from = document.state;
        document.state = parsed.decision === 'approved' ? 'approved' : 'inElaboration';
        document.updatedAt = approval.resolvedAt;
        this.#options.realtime?.emit(streams.project(document.projectId), 'document.stateChanged', {
          documentId: document.id,
          from,
          to: document.state,
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

  async saveDocumentVersion(id: Ulid, input: SaveDocumentVersionInput): Promise<DocumentVersion> {
    await this.#simulate();
    const parsed = saveDocumentVersionInputSchema.parse(input);
    const document = this.#require('documents', id);
    // Edição manual: a versão nasce com origem humana (`authorKind: 'user'`).
    const version: DocumentVersion = {
      id: this.#options.nextId(),
      documentId: document.id,
      version: document.currentVersion + 1,
      body: parsed.body,
      authorKind: 'user',
      authorId: this.#options.currentProfileId,
      createdAt: this.#options.now(),
    };
    this.#table('document-versions').set(version.id, version);
    document.currentVersion = version.version;
    document.updatedAt = version.createdAt;
    return structuredClone(version);
  }

  async classifyDocument(id: Ulid, input: ClassifyDocumentInput): Promise<Document> {
    await this.#simulate();
    const parsed = classifyDocumentInputSchema.parse(input);
    const document = this.#require('documents', id);
    if (parsed.classifications !== undefined) document.classifications = parsed.classifications;
    if (parsed.phaseName !== undefined) document.phaseName = parsed.phaseName;
    document.updatedAt = this.#options.now();
    return structuredClone(document);
  }

  async publishWorkflowVersion(
    templateId: Ulid,
    input: PublishWorkflowVersionInput,
  ): Promise<WorkflowVersion> {
    await this.#simulate();
    const parsed = publishWorkflowVersionInputSchema.parse(input);
    const template = this.#require('workflow-templates', templateId);
    const existing = [...this.#table('workflow-versions').values()].filter(
      (version) => version.templateId === templateId,
    );
    const version: WorkflowVersion = {
      id: this.#options.nextId(),
      templateId,
      version: existing.reduce((max, entry) => Math.max(max, entry.version), 0) + 1,
      phases: parsed.phases,
      gatesByPhase: parsed.gatesByPhase,
      phaseConfigs: parsed.phaseConfigs,
      defaultOperationMode: parsed.defaultOperationMode ?? null,
      transitions: parsed.transitions,
      changelog: parsed.changelog ?? null,
      state: 'published',
      publishedAt: this.#options.now(),
      archivedAt: null,
    };
    this.#table('workflow-versions').set(version.id, version);
    template.currentVersionId = version.id;
    if (template.state === 'draft') template.state = 'published';

    // Emite no stream global e nos projetos que usam o template.
    const payload = { templateId, versionId: version.id, version: version.version };
    this.#options.realtime?.emit(streams.global(), 'workflow.versionPublished', payload);
    const projectIds = new Set(
      [...this.#table('workflows').values()]
        .filter((workflow) => workflow.templateId === templateId)
        .map((workflow) => workflow.projectId),
    );
    for (const projectId of projectIds) {
      this.#options.realtime?.emit(streams.project(projectId), 'workflow.versionPublished', payload);
    }
    return structuredClone(version);
  }

  /* ---- gestão de templates de workflow (FR-4) ---- */

  async createWorkflowTemplate(input: CreateWorkflowTemplateInput): Promise<WorkflowTemplate> {
    await this.#simulate();
    const parsed = createWorkflowTemplateInputSchema.parse(input);
    const template: WorkflowTemplate = {
      id: this.#options.nextId(),
      name: parsed.name,
      description: parsed.description ?? '',
      currentVersionId: null,
      state: 'draft',
      archivedAt: null,
      createdAt: this.#options.now(),
    };
    this.#table('workflow-templates').set(template.id, template);
    return structuredClone(template);
  }

  async createWorkflowDraftVersion(
    templateId: Ulid,
    input?: WorkflowDraftInput,
  ): Promise<WorkflowVersion> {
    await this.#simulate();
    const parsed = workflowDraftInputSchema.parse(input ?? {});
    const template = this.#require('workflow-templates', templateId);
    if (template.state === 'archived') {
      throw ApiError.of(409, 'Template arquivado', 'Não é possível criar rascunho em template arquivado.');
    }
    const current = template.currentVersionId
      ? this.#table('workflow-versions').get(template.currentVersionId)
      : undefined;
    // Sem input: o rascunho nasce como cópia da versão vigente (editar publicado).
    const version: WorkflowVersion = {
      id: this.#options.nextId(),
      templateId,
      version: this.#nextVersionNumber(templateId),
      phases: parsed.phases ?? structuredClone(current?.phases ?? []),
      gatesByPhase: parsed.gatesByPhase ?? structuredClone(current?.gatesByPhase ?? {}),
      phaseConfigs:
        parsed.phaseConfigs ?? (current?.phaseConfigs ? structuredClone(current.phaseConfigs) : undefined),
      defaultOperationMode:
        parsed.defaultOperationMode !== undefined
          ? parsed.defaultOperationMode
          : (current?.defaultOperationMode ?? null),
      transitions: parsed.transitions ?? (current?.transitions ? structuredClone(current.transitions) : undefined),
      changelog: parsed.changelog ?? null,
      state: 'draft',
      publishedAt: null,
      archivedAt: null,
    };
    this.#table('workflow-versions').set(version.id, version);
    return structuredClone(version);
  }

  async updateWorkflowDraftVersion(
    versionId: Ulid,
    input: WorkflowDraftInput,
  ): Promise<WorkflowVersion> {
    await this.#simulate();
    const parsed = workflowDraftInputSchema.parse(input);
    const draft = this.#requireDraftVersion(versionId);
    if (parsed.phases !== undefined) draft.phases = parsed.phases;
    if (parsed.gatesByPhase !== undefined) draft.gatesByPhase = parsed.gatesByPhase;
    if (parsed.phaseConfigs !== undefined) draft.phaseConfigs = parsed.phaseConfigs;
    if (parsed.defaultOperationMode !== undefined) draft.defaultOperationMode = parsed.defaultOperationMode;
    if (parsed.transitions !== undefined) draft.transitions = parsed.transitions;
    if (parsed.changelog !== undefined) draft.changelog = parsed.changelog ?? null;
    return structuredClone(draft);
  }

  async publishWorkflowDraft(
    versionId: Ulid,
    input?: PublishWorkflowDraftInput,
  ): Promise<WorkflowVersion> {
    await this.#simulate();
    const parsed = publishWorkflowDraftInputSchema.parse(input ?? {});
    const draft = this.#requireDraftVersion(versionId);

    // Validação "do Harness": zod (payload completo de publicação) + regras
    // de domínio (fases, gates/transições/dependências). 422 bloqueia.
    const content = {
      phases: draft.phases,
      gatesByPhase: draft.gatesByPhase,
      phaseConfigs: draft.phaseConfigs,
      defaultOperationMode: draft.defaultOperationMode ?? null,
      transitions: draft.transitions,
    };
    try {
      publishWorkflowVersionInputSchema.parse({ ...content, changelog: draft.changelog ?? undefined });
    } catch {
      throw ApiError.of(422, 'Versão inválida', 'O rascunho não passou na validação do Harness (schema).');
    }
    const issues = validateWorkflowVersionContent(content);
    if (issues.length > 0) {
      throw ApiError.of(
        422,
        'Versão inválida',
        `O rascunho não passou na validação do Harness: ${issues.map((issue) => issue.key).join('; ')}`,
      );
    }

    const template = this.#require('workflow-templates', draft.templateId);
    draft.state = 'published';
    draft.publishedAt = this.#options.now();
    if (parsed.changelog !== undefined) draft.changelog = parsed.changelog || null;
    template.currentVersionId = draft.id;
    if (template.state === 'draft') template.state = 'published';

    const payload = { templateId: template.id, versionId: draft.id, version: draft.version };
    this.#options.realtime?.emit(streams.global(), 'workflow.versionPublished', payload);
    const projectIds = new Set(
      [...this.#table('workflows').values()]
        .filter((workflow) => workflow.templateId === template.id)
        .map((workflow) => workflow.projectId),
    );
    for (const projectId of projectIds) {
      this.#options.realtime?.emit(streams.project(projectId), 'workflow.versionPublished', payload);
    }
    return structuredClone(draft);
  }

  async archiveWorkflowTemplate(templateId: Ulid): Promise<WorkflowTemplate> {
    await this.#simulate();
    const template = this.#require('workflow-templates', templateId);
    if (template.state === 'archived') {
      throw ApiError.of(409, 'Template já arquivado', `Template ${templateId} já está arquivado.`);
    }
    template.state = 'archived';
    template.archivedAt = this.#options.now();
    return structuredClone(template);
  }

  async archiveWorkflowVersion(versionId: Ulid): Promise<WorkflowVersion> {
    await this.#simulate();
    const version = this.#require('workflow-versions', versionId);
    if (version.state === 'archived') {
      throw ApiError.of(409, 'Versão já arquivada', `Versão ${versionId} já está arquivada.`);
    }
    const template = this.#require('workflow-templates', version.templateId);
    if (template.currentVersionId === versionId) {
      throw ApiError.of(
        409,
        'Versão vigente',
        'A versão vigente do template não pode ser arquivada — publique outra versão antes.',
      );
    }
    version.state = 'archived';
    version.archivedAt = this.#options.now();
    return structuredClone(version);
  }

  async deleteWorkflowDraftVersion(versionId: Ulid): Promise<void> {
    await this.#simulate();
    const version = this.#requireDraftVersion(versionId);
    if (this.#isVersionUsed(versionId)) {
      throw ApiError.of(
        409,
        'Rascunho em uso',
        'Este rascunho está vinculado a um workflow/execução e não pode ser excluído — arquive-o.',
      );
    }
    this.#table('workflow-versions').delete(version.id);
  }

  async deleteWorkflowTemplate(templateId: Ulid): Promise<void> {
    await this.#simulate();
    const template = this.#require('workflow-templates', templateId);
    const inUse = [...this.#table('workflows').values()].some(
      (workflow) => workflow.templateId === templateId,
    );
    if (inUse) {
      throw ApiError.of(
        409,
        'Template em uso',
        'Template vinculado a projetos não pode ser excluído — arquive-o (tombstone).',
      );
    }
    const versions = [...this.#table('workflow-versions').values()].filter(
      (version) => version.templateId === templateId,
    );
    if (versions.some((version) => version.state !== 'draft' || this.#isVersionUsed(version.id))) {
      throw ApiError.of(
        409,
        'Template com versões publicadas',
        'Apenas templates rascunho nunca utilizados podem ser excluídos — arquive-o (tombstone).',
      );
    }
    for (const version of versions) this.#table('workflow-versions').delete(version.id);
    this.#table('workflow-templates').delete(template.id);
  }

  async duplicateWorkflowTemplate(templateId: Ulid): Promise<WorkflowTemplate> {
    await this.#simulate();
    const source = this.#require('workflow-templates', templateId);
    const copy = await this.createWorkflowTemplate({
      name: `${source.name} (cópia)`,
      description: source.description,
    });
    if (source.currentVersionId) {
      const current = this.#table('workflow-versions').get(source.currentVersionId);
      if (current) {
        const draft = await this.duplicateWorkflowVersion(current.id);
        const moved = this.#table('workflow-versions').get(draft.id)!;
        moved.templateId = copy.id;
      }
    }
    return structuredClone(this.#table('workflow-templates').get(copy.id)!);
  }

  async duplicateWorkflowVersion(versionId: Ulid): Promise<WorkflowVersion> {
    await this.#simulate();
    const source = this.#require('workflow-versions', versionId);
    const copy: WorkflowVersion = {
      ...structuredClone(source),
      id: this.#options.nextId(),
      version: this.#nextVersionNumber(source.templateId),
      changelog: null,
      state: 'draft',
      publishedAt: null,
      archivedAt: null,
    };
    this.#table('workflow-versions').set(copy.id, copy);
    return structuredClone(copy);
  }

  async linkWorkflowTemplate(input: LinkWorkflowTemplateInput): Promise<Workflow> {
    await this.#simulate();
    const parsed = linkWorkflowTemplateInputSchema.parse(input);
    this.#require('projects', parsed.projectId);
    const template = this.#require('workflow-templates', parsed.templateId);
    const existing = [...this.#table('workflows').values()].find(
      (workflow) => workflow.projectId === parsed.projectId,
    );
    if (existing) {
      throw ApiError.of(
        409,
        'Projeto já tem workflow',
        'O projeto já tem um workflow vinculado — troque a versão ativa em vez de vincular outro.',
      );
    }
    const versionId = parsed.versionId ?? template.currentVersionId;
    if (versionId === null) {
      throw ApiError.of(
        409,
        'Template sem versão publicada',
        'Publique uma versão do template antes de vinculá-lo ao projeto.',
      );
    }
    const version = this.#require('workflow-versions', versionId);
    if (version.templateId !== template.id || version.state !== 'published') {
      throw ApiError.of(409, 'Versão inválida', 'A versão precisa estar publicada e pertencer ao template.');
    }
    const workflow: Workflow = {
      id: this.#options.nextId(),
      projectId: parsed.projectId,
      templateId: template.id,
      activeVersionId: versionId,
      operationMode: version.defaultOperationMode ?? 'manual',
      semiautonomousPauseGates: [],
      riskAcceptances: [],
      createdAt: this.#options.now(),
    };
    this.#table('workflows').set(workflow.id, workflow);
    this.#appendAudit(
      'user',
      this.#options.currentProfileId,
      'workflow.templateLinked',
      'project',
      parsed.projectId,
      `Template "${template.name}" vinculado (v${version.version}).`,
    );
    return structuredClone(workflow);
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

    if (CHIEF_PLAN_TRIGGER.test(parsed.content)) {
      this.#scheduleChiefPlan(project, parsed.content);
    }

    return {
      turnId,
      conversationId: conversation.id,
      state: 'pending',
      correlationId: `turn:${turnId}`,
      readiness: { overallState: 'Ready', executionState: 'Ready' },
      blockers: [],
      nextActions: [],
      links: {
        readiness: `/api/v1/projects/${project.id}/readiness`,
        conversation: `/api/v1/conversations/${conversation.id}`,
      },
    };
  }

  /* ---- comandos do chefe (orquestração) ---- */

  async pauseChief(projectId: Ulid): Promise<Project> {
    await this.#simulate();
    const project = this.#require('projects', projectId);
    project.state = 'paused';
    project.lastActivityAt = this.#options.now();
    const chief = this.#table('agents').get(project.chiefAgentId);
    if (chief && chief.state !== 'waiting') {
      const from = chief.state;
      chief.state = 'waiting';
      this.#options.realtime?.emit(streams.global(), 'agent.statusChanged', {
        agentId: chief.id,
        from,
        to: 'waiting',
        currentTaskId: chief.currentTaskId,
      });
    }
    this.#appendAudit('user', this.#options.currentProfileId, 'chief.paused', 'project', project.id, `Orquestração do projeto ${project.key} pausada.`);
    return structuredClone(project);
  }

  async resumeChief(projectId: Ulid): Promise<Project> {
    await this.#simulate();
    const project = this.#require('projects', projectId);
    project.state = 'active';
    project.lastActivityAt = this.#options.now();
    const chief = this.#table('agents').get(project.chiefAgentId);
    if (chief && chief.state === 'waiting') {
      chief.state = 'idle';
      this.#options.realtime?.emit(streams.global(), 'agent.statusChanged', {
        agentId: chief.id,
        from: 'waiting',
        to: 'idle',
        currentTaskId: chief.currentTaskId,
      });
    }
    this.#appendAudit('user', this.#options.currentProfileId, 'chief.resumed', 'project', project.id, `Orquestração do projeto ${project.key} retomada.`);
    return structuredClone(project);
  }

  async handoffChief(projectId: Ulid, input: HandoffChiefInput): Promise<Agent> {
    await this.#simulate();
    const parsed = handoffChiefInputSchema.parse(input);
    const project = this.#require('projects', projectId);
    const oldChief = this.#require('agents', project.chiefAgentId);
    const definitionId = parsed.targetDefinitionId ?? oldChief.definitionId;
    this.#require('agent-definitions', definitionId);
    if (parsed.targetModelId) this.#require('models', parsed.targetModelId);

    const now = this.#options.now();
    const nextFencing = (oldChief.lease?.fencingToken ?? 0) + 1;
    if (oldChief.lease) oldChief.lease = null;
    if (oldChief.state !== 'idle') {
      const from = oldChief.state;
      oldChief.state = 'idle';
      oldChief.currentTaskId = null;
      this.#options.realtime?.emit(streams.global(), 'agent.statusChanged', {
        agentId: oldChief.id,
        from,
        to: 'idle',
        currentTaskId: null,
      });
    }

    const newChief: Agent = {
      id: this.#options.nextId(),
      definitionId,
      projectId: project.id,
      name: oldChief.name,
      state: 'idle',
      currentTaskId: null,
      modelId: parsed.targetModelId ?? null,
      lease: { fencingToken: nextFencing, expiresAt: new Date(Date.parse(now) + 60_000).toISOString() },
      metrics: { tasksCompleted: 0, tokensInput: 0, tokensOutput: 0, costUsd: 0, uptimeMs: 0 },
      lastHeartbeatAt: now,
    };
    this.#table('agents').set(newChief.id, newChief);
    project.chiefAgentId = newChief.id;
    project.lastActivityAt = now;
    this.#options.realtime?.emit(streams.global(), 'agent.statusChanged', {
      agentId: newChief.id,
      from: 'idle',
      to: 'idle',
      currentTaskId: null,
    });
    this.#appendAudit('user', this.#options.currentProfileId, 'chief.handedOff', 'project', project.id, `Bastão passado para nova instância (fencing ${nextFencing}). Motivo: ${parsed.note}`);
    return structuredClone(newChief);
  }

  async drainChiefTasks(projectId: Ulid, input: DrainChiefTasksInput): Promise<number> {
    await this.#simulate();
    const parsed = drainChiefTasksInputSchema.parse(input);
    const project = this.#require('projects', projectId);
    const note = parsed.note ?? null;
    let drained = 0;
    const activeStates = new Set(['development', 'review', 'corrections', 'testsGates']);
    for (const task of this.#table('tasks').values()) {
      if (task.projectId !== project.id || !activeStates.has(task.state)) continue;
      const from = task.state;
      task.state = 'ready';
      task.updatedAt = this.#options.now();
      this.#options.realtime?.emit(streams.project(project.id), 'task.stateChanged', {
        taskId: task.id,
        from,
        to: 'ready',
        changedByKind: 'user',
        note,
      });
      drained += 1;
    }
    for (const attempt of this.#table('attempts').values()) {
      if (attempt.state !== 'running') continue;
      const task = this.#table('tasks').get(attempt.taskId);
      if (task?.projectId !== project.id) continue;
      attempt.state = 'cancelled';
      attempt.finishedAt = this.#options.now();
      attempt.durationMs = Date.parse(attempt.finishedAt) - Date.parse(attempt.startedAt);
    }
    for (const agent of this.#table('agents').values()) {
      if (agent.projectId !== project.id || !['working', 'waiting'].includes(agent.state)) continue;
      const from = agent.state;
      agent.state = 'idle';
      agent.currentTaskId = null;
      this.#options.realtime?.emit(streams.global(), 'agent.statusChanged', {
        agentId: agent.id,
        from,
        to: 'idle',
        currentTaskId: null,
      });
    }
    this.#appendAudit('user', this.#options.currentProfileId, 'chief.tasksDrained', 'project', project.id, `${drained} tarefa(s) drenadas para "ready".${note ? ` Nota: ${note}` : ''}`);
    return drained;
  }

  /* ---- rodar projeto (run-targets) ---- */

  async startRunTarget(runTargetId: Ulid): Promise<RunTarget> {
    await this.#simulate();
    const target = this.#require('run-targets', runTargetId);
    target.state = 'running';
    target.lastCheckAt = this.#options.now();
    const where = target.port ? ` na porta ${target.port}` : '';
    this.#emitRunLog(target, `Iniciando "${target.name}"${where}…`);
    setTimeout(() => {
      const health = target.url ?? (target.port ? `porta ${target.port}` : 'processo');
      this.#emitRunLog(target, `"${target.name}" pronto — health check OK em ${health}.`);
    }, RUN_LOG_STEP_MS);
    setTimeout(() => {
      this.#emitRunLog(target, `"${target.name}" aguardando requisições.`);
    }, RUN_LOG_STEP_MS * 2);
    return structuredClone(target);
  }

  async stopRunTarget(runTargetId: Ulid): Promise<RunTarget> {
    await this.#simulate();
    const target = this.#require('run-targets', runTargetId);
    target.state = 'stopped';
    target.lastCheckAt = this.#options.now();
    this.#emitRunLog(target, `Encerrando "${target.name}"…`);
    setTimeout(() => {
      this.#emitRunLog(target, `"${target.name}" parado.`);
    }, RUN_LOG_STEP_MS);
    return structuredClone(target);
  }

  async restartRunTarget(runTargetId: Ulid): Promise<RunTarget> {
    await this.#simulate();
    const target = this.#require('run-targets', runTargetId);
    target.state = 'running';
    target.lastCheckAt = this.#options.now();
    this.#emitRunLog(target, `Reiniciando "${target.name}"…`);
    setTimeout(() => {
      const where = target.port ? ` na porta ${target.port}` : '';
      this.#emitRunLog(target, `"${target.name}" pronto novamente${where} — health check OK.`);
    }, RUN_LOG_STEP_MS);
    return structuredClone(target);
  }

  async cleanupRunEnvironment(projectId: Ulid): Promise<number> {
    await this.#simulate();
    const project = this.#require('projects', projectId);
    let stopped = 0;
    for (const target of this.#table('run-targets').values()) {
      if (target.projectId !== project.id || target.state === 'stopped') continue;
      target.state = 'stopped';
      target.lastCheckAt = this.#options.now();
      this.#emitRunLog(target, `Cleanup: "${target.name}" parado.`);
      stopped += 1;
    }
    this.#emitRunLog(
      { projectId: project.id },
      `Cleanup do ambiente concluído — ${stopped} serviço(s) parado(s).`,
    );
    this.#appendAudit('user', this.#options.currentProfileId, 'run.environmentCleaned', 'project', project.id, `Cleanup do ambiente: ${stopped} serviço(s) parado(s).`);
    return stopped;
  }

  /* ---- providers ---- */

  async syncProviderCatalog(providerId: Ulid): Promise<Model[]> {
    await this.#simulate();
    this.#require('providers', providerId);
    const models = [...this.#table('models').values()].filter(
      (model) => model.providerId === providerId,
    );
    // Sincronização "movimenta" as cotas: emite quota.updated por conta.
    for (const account of this.#table('accounts').values()) {
      if (account.providerId !== providerId) continue;
      this.#options.realtime?.emit(streams.global(), 'quota.updated', {
        accountId: account.id,
        budgetId: null,
        usedUsd: account.quotaUsedUsd,
        limitUsd: account.quotaLimitUsd,
      });
    }
    this.#appendAudit('user', this.#options.currentProfileId, 'provider.catalogSynced', 'provider', providerId, `Catálogo sincronizado: ${models.length} modelo(s).`);
    return structuredClone(models);
  }

  /* ---- contas de provider (FR-5) ---- */

  async createAccount(input: CreateAccountInput): Promise<Account> {
    await this.#simulate();
    const parsed = createAccountInputSchema.parse(input);
    this.#require('providers', parsed.providerId);
    const account: Account = {
      id: this.#options.nextId(),
      providerId: parsed.providerId,
      label: parsed.label,
      state: 'disabled',
      quotaLimitUsd: parsed.quotaLimitUsd ?? null,
      quotaUsedUsd: 0,
      identity: parsed.identity ?? null,
      plan: parsed.plan,
      authentication: parsed.authentication,
      health: 'unknown',
      quotaWindow: parsed.quotaWindow,
      quotaResetsAt: parsed.quotaResetsAt ?? null,
      capabilities: parsed.capabilities,
    };
    this.#table('accounts').set(account.id, account);
    this.#appendAudit('user', this.#options.currentProfileId, 'account.created', 'account', account.id, `Conta "${account.label}" criada.`);
    return structuredClone(account);
  }

  async updateAccount(id: Ulid, input: UpdateAccountInput): Promise<Account> {
    await this.#simulate();
    const parsed = updateAccountInputSchema.parse(input);
    const account = this.#require('accounts', id);
    if (parsed.label !== undefined) account.label = parsed.label;
    if (parsed.state !== undefined) account.state = parsed.state;
    if (parsed.quotaLimitUsd !== undefined) account.quotaLimitUsd = parsed.quotaLimitUsd;
    if (parsed.identity !== undefined) account.identity = parsed.identity;
    if (parsed.plan !== undefined) account.plan = parsed.plan;
    if (parsed.authentication !== undefined) account.authentication = parsed.authentication;
    if (parsed.health !== undefined) account.health = parsed.health;
    if (parsed.quotaWindow !== undefined) account.quotaWindow = parsed.quotaWindow;
    if (parsed.quotaResetsAt !== undefined) account.quotaResetsAt = parsed.quotaResetsAt;
    if (parsed.capabilities !== undefined) account.capabilities = parsed.capabilities;
    this.#appendAudit('user', this.#options.currentProfileId, 'account.updated', 'account', account.id, `Conta "${account.label}" atualizada.`);
    return structuredClone(account);
  }

  async enableAccount(id: Ulid): Promise<Account> {
    await this.#simulate();
    const account = this.#require('accounts', id);
    account.state = 'active';
    return structuredClone(account);
  }

  async disableAccount(id: Ulid): Promise<Account> {
    await this.#simulate();
    const account = this.#require('accounts', id);
    account.state = 'disabled';
    return structuredClone(account);
  }

  async deleteAccount(id: Ulid): Promise<void> {
    await this.#simulate();
    const account = this.#require('accounts', id);
    if (account.state !== 'disabled') {
      throw ApiError.of(
        409,
        'Conta ativa',
        'Somente uma conta desabilitada pode ser removida.',
      );
    }
    const blockingBudget = [...this.#table('budgets').values()].find(
      (budget) => budget.scope === 'account' && budget.scopeId === id,
    );
    if (blockingBudget) {
      throw ApiError.of(
        409,
        'Conta em uso',
        'A conta possui budget vinculado — remova o budget antes de excluir a conta.',
      );
    }
    const blockingDefinition = [...this.#table('agent-definitions').values()].find(
      (definition) => definition.preferredAccountId === id,
    );
    if (blockingDefinition) {
      throw ApiError.of(
        409,
        'Conta em uso',
        `A conta é a preferencial da definição "${blockingDefinition.name}" — remova a referência antes de excluir.`,
      );
    }
    // Nota: políticas de roteamento referenciam MODELOS (não contas) — não há
    // vínculo direto conta↔roteamento no contrato atual (documentado no HANDOFF).
    this.#table('accounts').delete(id);
    this.#appendAudit('user', this.#options.currentProfileId, 'account.deleted', 'account', id, `Conta "${account.label}" removida.`);
  }

  /* ---- definições de agente (FR-5) ---- */

  async createAgentDefinition(input: CreateAgentDefinitionInput): Promise<AgentDefinition> {
    await this.#simulate();
    const parsed = createAgentDefinitionInputSchema.parse(input);
    const definition: AgentDefinition = {
      id: this.#options.nextId(),
      key: parsed.key,
      name: parsed.name,
      role: parsed.role,
      specialty: parsed.specialty ?? null,
      description: parsed.description,
      defaultModelId: parsed.defaultModelId ?? null,
      skillIds: parsed.skillIds,
      toolIds: parsed.toolIds,
      state: 'enabled',
      persona: parsed.persona ?? null,
      mission: parsed.mission ?? null,
      responsibilities: parsed.responsibilities ?? null,
      instructions: parsed.instructions ?? null,
      restrictions: parsed.restrictions ?? null,
      bestPractices: parsed.bestPractices ?? null,
      stacks: parsed.stacks,
      defaultEffort: parsed.defaultEffort ?? null,
      preferredAccountId: parsed.preferredAccountId ?? null,
      fallbackModelIds: parsed.fallbackModelIds,
      team: parsed.team ?? null,
      actorCritic: parsed.actorCritic ?? null,
      risk: parsed.risk ?? null,
      version: 1,
      history: [],
    };
    this.#table('agent-definitions').set(definition.id, definition);
    this.#appendAudit('user', this.#options.currentProfileId, 'agentDefinition.created', 'agent-definition', definition.id, `Definição "${definition.name}" criada (v1).`);
    return structuredClone(definition);
  }

  async updateAgentDefinition(
    id: Ulid,
    input: UpdateAgentDefinitionInput,
  ): Promise<AgentDefinition> {
    await this.#simulate();
    const parsed = updateAgentDefinitionInputSchema.parse(input);
    const definition = this.#require('agent-definitions', id);
    if (definition.state === 'archived') {
      throw ApiError.of(409, 'Definição arquivada', 'Definições arquivadas não podem ser editadas.');
    }
    if (
      parsed.expectedVersion !== undefined &&
      parsed.expectedVersion !== 0 &&
      parsed.expectedVersion !== (definition.version ?? 1)
    ) {
      throw ApiError.of(409, 'Versão desatualizada', 'A definição foi alterada por outra sessão.');
    }
    const record = definition as unknown as Record<string, unknown>;
    const changedFields = (Object.keys(parsed) as (keyof UpdateAgentDefinitionInput)[]).filter(
      (field) =>
        field !== 'expectedVersion' &&
        parsed[field] !== undefined &&
        JSON.stringify(parsed[field]) !== JSON.stringify(record[field]),
    );
    for (const field of changedFields) {
      record[field] = parsed[field];
    }
    if (changedFields.length > 0) {
      // Versionamento como os demais recursos: version incrementa a cada
      // edição real e o histórico registra os campos alterados.
      definition.version = (definition.version ?? 1) + 1;
      definition.history = [
        ...(definition.history ?? []),
        {
          version: definition.version,
          changedAt: this.#options.now(),
          changedFields: [...changedFields],
          summary: `Campos alterados: ${changedFields.join(', ')}.`,
        },
      ];
    }
    this.#appendAudit('user', this.#options.currentProfileId, 'agentDefinition.updated', 'agent-definition', definition.id, `Definição "${definition.name}" atualizada (v${definition.version ?? 1}).`);
    return structuredClone(definition);
  }

  async duplicateAgentDefinition(
    id: Ulid,
    input?: DuplicateAgentDefinitionInput,
  ): Promise<AgentDefinition> {
    await this.#simulate();
    const source = this.#require('agent-definitions', id);
    const copy: AgentDefinition = {
      ...structuredClone(source),
      id: this.#options.nextId(),
      key: input?.key ?? `${source.key}-copia`,
      name: input?.name ?? `${source.name} (cópia)`,
      state: 'enabled',
      version: 1,
      history: [],
    };
    this.#table('agent-definitions').set(copy.id, copy);
    this.#appendAudit('user', this.#options.currentProfileId, 'agentDefinition.duplicated', 'agent-definition', copy.id, `Definição "${source.name}" duplicada como "${copy.name}".`);
    return structuredClone(copy);
  }

  async enableAgentDefinition(id: Ulid): Promise<AgentDefinition> {
    await this.#simulate();
    const definition = this.#require('agent-definitions', id);
    definition.state = 'enabled';
    return structuredClone(definition);
  }

  async disableAgentDefinition(id: Ulid): Promise<AgentDefinition> {
    await this.#simulate();
    const definition = this.#require('agent-definitions', id);
    definition.state = 'disabled';
    return structuredClone(definition);
  }

  async archiveAgentDefinition(id: Ulid): Promise<AgentDefinition> {
    await this.#simulate();
    const definition = this.#require('agent-definitions', id);
    definition.state = 'archived';
    this.#appendAudit('user', this.#options.currentProfileId, 'agentDefinition.archived', 'agent-definition', definition.id, `Definição "${definition.name}" arquivada.`);
    return structuredClone(definition);
  }

  async deleteAgentDefinition(id: Ulid): Promise<void> {
    await this.#simulate();
    const definition = this.#require('agent-definitions', id);
    const used = [...this.#table('agents').values()].some((agent) => agent.definitionId === id);
    if (used) {
      throw ApiError.of(
        409,
        'Definição em uso',
        'A definição já possui instâncias de agente — apenas arquivamento é permitido.',
      );
    }
    this.#table('agent-definitions').delete(id);
    this.#appendAudit('user', this.#options.currentProfileId, 'agentDefinition.deleted', 'agent-definition', id, `Definição "${definition.name}" excluída (nunca utilizada).`);
  }

  /* ---- PO Assistant ---- */

  async analyzeSolicitation(input: AnalyzeSolicitationInput): Promise<SolicitationAnalysis> {
    await this.#simulate();
    const parsed = analyzeSolicitationInputSchema.parse(input);
    this.#require('projects', parsed.projectId);

    const firstLine = parsed.text.split('\n').find((line) => line.trim() !== '') ?? parsed.text;
    const solicitation = await this.create('solicitations', {
      projectId: parsed.projectId,
      kind: 'request',
      title: firstLine.trim().slice(0, 60),
      body:
        parsed.attachmentNames && parsed.attachmentNames.length > 0
          ? `${parsed.text}\n\nAnexos: ${parsed.attachmentNames.join(', ')}`
          : parsed.text,
    });

    const item = (text: string) => ({ id: this.#options.nextId(), text });
    const sentences = parsed.text
      .split(/[.\n;]+/)
      .map((sentence) => sentence.trim())
      .filter((sentence) => sentence.length > 3)
      .slice(0, 5);

    const ambiguities: string[] = [];
    if (/\betc\b/i.test(parsed.text)) {
      ambiguities.push('"etc." deixa o escopo aberto — listar os casos concretos.');
    }
    if (/\b(rápid[oa]|urgente|logo)\b/i.test(parsed.text)) {
      ambiguities.push('Prazo subjetivo ("rápido/urgente") — definir uma data-alvo.');
    }
    const contradictions = PO_ANALYSIS_CONTRADICTION_TRIGGER.test(parsed.text)
      ? ['O próprio pedido sinaliza uma contradição — revisar com o solicitante.']
      : [];
    const questions = [PO_ANALYSIS_QUESTION_DEADLINE, PO_ANALYSIS_QUESTION_USERS];
    if (!/\d/.test(parsed.text)) {
      questions.push('Há metas quantitativas (volume, SLA, limite) para esta entrega?');
    }

    return {
      solicitationId: solicitation.id,
      requirements: (sentences.length > 0 ? sentences : [parsed.text.trim()]).map(item),
      ambiguities: ambiguities.map(item),
      contradictions: contradictions.map(item),
      questions: questions.map(item),
      acceptanceCriteria: [
        item('Entrega demonstrada e validada com o solicitante.'),
        item(PO_ANALYSIS_CRITERION_REVIEW),
      ],
    };
  }

  /* ---- licença, backup, diagnóstico ---- */

  async activateLicense(input: ActivateLicenseInput): Promise<License> {
    await this.#simulate();
    let parsed: ActivateLicenseInput;
    try {
      parsed = activateLicenseInputSchema.parse(input);
    } catch {
      throw ApiError.of(400, 'Chave inválida', 'A chave deve ter o formato XXXX-XXXX-XXXX-XXXX.', {
        key: ['Formato esperado: XXXX-XXXX-XXXX-XXXX.'],
      });
    }
    const now = this.#options.now();
    let license = [...this.#table('licenses').values()][0];
    if (!license) {
      license = {
        id: this.#options.nextId(),
        state: 'unlicensed',
        plan: 'Pro',
        deviceId: 'dispositivo-local',
        deviceName: 'Este dispositivo',
        expiresAt: null,
        gracePeriodEndsAt: null,
        offlineMode: false,
        lastValidatedAt: null,
      };
      this.#table('licenses').set(license.id, license);
    }
    license.state = 'active';
    license.lastValidatedAt = now;
    license.offlineMode = false;
    if (license.expiresAt === null || Date.parse(license.expiresAt) < Date.parse(now)) {
      const expires = new Date(Date.parse(now));
      expires.setFullYear(expires.getFullYear() + 1);
      license.expiresAt = expires.toISOString();
    }
    this.#appendAudit('user', this.#options.currentProfileId, 'license.activated', 'license', license.id, `Licença ${license.plan} ativada neste dispositivo (chave ${parsed.key.slice(0, 4)}-****-****-****).`);
    return structuredClone(license);
  }

  async createBackup(): Promise<BackupHandle> {
    await this.#simulate();
    const handle: BackupHandle = { backupId: this.#options.nextId(), createdAt: this.#options.now() };
    this.#appendAudit('user', this.#options.currentProfileId, 'backup.created', 'backup', handle.backupId, 'Backup local criado.');
    return handle;
  }

  async restoreBackup(backupId: Ulid): Promise<void> {
    await this.#simulate();
    this.#appendAudit('user', this.#options.currentProfileId, 'backup.restored', 'backup', backupId, 'Backup local restaurado (mock: dados permanecem como estão).');
  }

  /**
   * Cria a instância do Chief do projeto recém-criado, reusando a definição de
   * papel `chief` já existente no catálogo (não inventa definição nova).
   */
  #provisionChiefAgent(project: Project): void {
    if (this.#table('agents').get(project.chiefAgentId)) return;
    const definition = [...this.#table('agent-definitions').values()].find(
      (entry) => entry.role === 'chief',
    );
    if (!definition) return;
    const now = this.#options.now();
    const agent: Agent = {
      id: project.chiefAgentId,
      definitionId: definition.id,
      projectId: project.id,
      name: `Chefe — ${project.name}`,
      state: 'idle',
      currentTaskId: null,
      modelId: null,
      lease: null,
      metrics: { costUsd: 0, tokensInput: 0, tokensOutput: 0, tasksCompleted: 0, uptimeMs: 0 },
      lastHeartbeatAt: now,
    };
    this.#table('agents').set(agent.id, agent);
  }

  /**
   * Prontidão do projeto — espelha o avaliador canônico do backend
   * (`ReadinessEvaluator`, ADR-017) sobre o store do mock.
   *
   * No mock, provedor/modelo/chief são fixtures: as dependências presentes são
   * marcadas como `Simulated`, e não como `Ready`. É por isso que o modo mock
   * mostra "Modo simulado" — a UI nunca apresenta fixture como execução real.
   */
  async getProjectReadiness(projectId: Ulid): Promise<ProjectReadinessSnapshot> {
    await this.#simulate();
    const project = this.#table('projects').get(projectId);
    if (!project) {
      throw ApiError.of(404, 'project_not_found', 'The project does not exist.');
    }

    const organizationReady = this.#table('organizations').size > 0;
    const enabledProviderIds = new Set(
      [...this.#table('providers').values()].filter((p) => p.enabled).map((p) => p.id),
    );
    const account = [...this.#table('accounts').values()].find(
      (entry) => entry.state === 'active' && enabledProviderIds.has(entry.providerId),
    );
    const model = [...this.#table('models').values()].find(
      (entry) => entry.enabled && enabledProviderIds.has(entry.providerId),
    );
    const workflowBound = [...this.#table('workflows').values()].some(
      (entry) => entry.projectId === projectId,
    );
    const chief = [...this.#table('agents').values()].find(
      (entry) => entry.id === project.chiefAgentId,
    );
    const chiefDefinition = chief
      ? [...this.#table('agent-definitions').values()].find((d) => d.id === chief.definitionId)
      : undefined;
    const chiefModelId = chief?.modelId ?? chiefDefinition?.defaultModelId ?? null;
    const chiefModelResolves =
      chiefModelId !== null && this.#table('models').get(chiefModelId) !== undefined;
    const chiefHealthy = chief ? chief.state !== 'error' && chief.state !== 'outOfQuota' : false;

    return buildMockReadinessSnapshot({
      projectId,
      organizationReady,
      accountId: account?.id ?? null,
      modelId: model?.id ?? null,
      workflowBound,
      chiefAgentId: chief?.id ?? null,
      chiefHealthy,
      chiefModelResolves,
    });
  }

  async getDiagnostics(): Promise<Diagnostics> {
    await this.#simulate();
    const now = this.#options.now();
    const license = [...this.#table('licenses').values()][0] ?? null;
    const settings = [...this.#table('settings').values()].find(
      (entry) => entry.profileId === this.#options.currentProfileId,
    );
    const realtimeState = this.#options.realtime?.state ?? 'disconnected';
    return {
      product: { name: product.name, version: product.version, codename: product.codename },
      apiMode: 'mock',
      realtimeState,
      checks: [
        { key: 'api', state: 'ok', detail: 'Camada de dados em memória (mock) respondendo.' },
        {
          key: 'realtime',
          state: realtimeState === 'connected' ? 'ok' : 'warning',
          detail: `Tempo real ${realtimeState === 'connected' ? 'conectado' : 'não conectado'} (mock).`,
        },
        {
          key: 'license',
          state: license?.state === 'active' ? 'ok' : 'warning',
          detail: license ? `Licença ${license.plan}: ${license.state}.` : 'Nenhuma licença ativada.',
        },
        {
          key: 'sandbox',
          state: settings?.unsafeModeAcceptedAt ? 'warning' : 'ok',
          detail: settings?.unsafeModeAcceptedAt
            ? 'Modo inseguro aceito — execução fora do sandbox.'
            : 'Execução em sandbox (modo inseguro não aceito).',
        },
      ],
      generatedAt: now,
    };
  }

  listGovernanceReceipts(): Promise<GovernanceReceipt[]> {
    return this.#governanceRequiresHttp();
  }

  getGovernanceReceipt(): Promise<GovernanceReceipt> {
    return this.#governanceRequiresHttp();
  }

  listGovernanceMetrics(): Promise<GovernanceMetric[]> {
    return this.#governanceRequiresHttp();
  }

  createFreshContextEvaluation(): Promise<EvaluationResult> {
    return this.#governanceRequiresHttp();
  }

  listStaleDocumentFindings(): Promise<StaleDocumentFinding[]> {
    return this.#governanceRequiresHttp();
  }

  applyHashlinePatch(): Promise<HashlinePatchResult> {
    return this.#governanceRequiresHttp();
  }

  listHashlineBenchmark(): Promise<PatchBenchmark[]> {
    return this.#governanceRequiresHttp();
  }

  listAgentExecutors(): Promise<AgentExecutor[]> {
    return this.#governanceRequiresHttp();
  }

  listLearningCandidates(): Promise<LearningCandidatePage> {
    return this.#governanceRequiresHttp();
  }

  getLearningCandidate(): Promise<LearningCandidate> {
    return this.#governanceRequiresHttp();
  }

  listLearningCandidateEvidence(): Promise<LearningEvidenceRecord[]> {
    return this.#governanceRequiresHttp();
  }

  compareLearningCandidate(): Promise<LearningCandidateComparison> {
    return this.#governanceRequiresHttp();
  }

  listLearningCandidateHistory(): Promise<LearningCandidateHistoryRecord[]> {
    return this.#governanceRequiresHttp();
  }

  getLearningCandidateMetrics(): Promise<LearningCandidateMetrics> {
    return this.#governanceRequiresHttp();
  }

  transitionLearningCandidate(): Promise<LearningCandidate> {
    return this.#governanceRequiresHttp();
  }

  evaluateLearningCandidate(): Promise<LearningCandidate> {
    return this.#governanceRequiresHttp();
  }

  shadowLearningCandidate(): Promise<LearningCandidate> {
    return this.#governanceRequiresHttp();
  }

  decideLearningCandidate(): Promise<LearningCandidate> {
    return this.#governanceRequiresHttp();
  }

  async listGovernanceDocs(): Promise<GovernanceDocTree> {
    await this.#simulate();
    const files = [...this.#governanceDocs.entries()]
      .map(([path, entry]) => ({
        path,
        name: path.slice(path.lastIndexOf('/') + 1),
        size: new TextEncoder().encode(entry.content).length,
        modifiedAt: entry.modifiedAt,
      }))
      .sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
    return { files, roots: ['governance', 'docs'] };
  }

  async readGovernanceDoc(path: string): Promise<GovernanceDocContent> {
    await this.#simulate();
    this.#assertGovernanceDocPath(path);
    const entry = this.#governanceDocs.get(path);
    if (!entry) {
      throw ApiError.of(404, 'Documento não encontrado', `O documento ${path} não existe.`);
    }
    return this.#toGovernanceDocContent(path, entry.content, entry.modifiedAt);
  }

  async saveGovernanceDoc(path: string, content: string): Promise<GovernanceDocContent> {
    await this.#simulate();
    this.#assertGovernanceDocPath(path);
    const modifiedAt = this.#options.now();
    this.#governanceDocs.set(path, { content, modifiedAt });
    return this.#toGovernanceDocContent(path, content, modifiedAt);
  }

  async deleteGovernanceDoc(path: string): Promise<void> {
    await this.#simulate();
    this.#assertGovernanceDocPath(path);
    if (!this.#governanceDocs.delete(path)) {
      throw ApiError.of(404, 'Documento não encontrado', `O documento ${path} não existe.`);
    }
  }

  #toGovernanceDocContent(path: string, content: string, modifiedAt: string): GovernanceDocContent {
    return {
      path,
      name: path.slice(path.lastIndexOf('/') + 1),
      content,
      size: new TextEncoder().encode(content).length,
      modifiedAt,
    };
  }

  /** Espelha o allowlist/anti-traversal do backend para o perfil mock. */
  #assertGovernanceDocPath(path: string): void {
    const invalid = () =>
      ApiError.of(400, 'Path inválido', 'O path escapa do allowlist de governança.');
    if (!path || path.trim().length === 0) throw invalid();
    const normalized = path.replace(/\\/g, '/');
    if (normalized.startsWith('/')) throw invalid();
    const segments = normalized.split('/');
    for (const segment of segments) {
      if (segment.length === 0 || segment === '.' || segment === '..') throw invalid();
    }
    if (segments[0] !== 'governance' && segments[0] !== 'docs') throw invalid();
  }

  /**
   * Roster de execução REDIGIDO. O perfil mock devolve as 7 identidades canônicas
   * (ADR-021) — apenas alias/provider/executor/papéis/estado, jamais credencial ou token.
   */
  async listAgentAccounts(): Promise<AgentAccountRoster[]> {
    await this.#simulate();
    return MOCK_AGENT_ROSTER.map((account) => ({ ...account, roles: [...account.roles] }));
  }

  async listChannelLinks(): Promise<ChannelLink[]> {
    await this.#simulate();
    const project = [...this.#table('projects').values()][0] ?? null;
    if (!project) return [];
    const conversation = [...this.#table('conversations').values()].find(
      (entry) => entry.projectId === project.id,
    );
    return [
      {
        id: '01J0CHANNELTELEGRAM000000001',
        kind: 'telegram',
        externalIdentity: '@poseidon_ops_bot:512044',
        projectId: project.id,
        conversationId: conversation?.id ?? project.id,
        linkedAt: this.#options.now(),
      },
      ...this.#channelLinks,
    ];
  }

  async createChannelLink(input: CreateChannelLinkInput): Promise<ChannelLink> {
    await this.#simulate();
    const project = this.#table('projects').get(input.projectId);
    if (!project) throw this.#notFound('projects', input.projectId);
    const identity = input.externalIdentity.trim();
    // Idempotente por identidade, como o backend.
    const existing = this.#channelLinks.find(
      (link) => link.kind === input.kind && link.externalIdentity === identity,
    );
    if (existing) return existing;
    const link: ChannelLink = {
      id: this.#options.nextId(),
      kind: input.kind,
      externalIdentity: identity,
      projectId: input.projectId,
      conversationId: this.#options.nextId(),
      linkedAt: this.#options.now(),
    };
    this.#channelLinks.push(link);
    return link;
  }

  async listChannelMessages(): Promise<ChannelMessagePage> {
    await this.#simulate();
    const now = this.#options.now();
    return {
      items: [
        {
          id: '01J0CHANNELMSG0000000000001',
          authorRole: 'user',
          content: 'Qual o status do golden path?',
          createdAt: now,
        },
        {
          id: '01J0CHANNELMSG0000000000002',
          authorRole: 'agent',
          content: 'Portfólio verde; 2 tarefas em desenvolvimento e 1 aguardando gate.',
          createdAt: now,
        },
      ],
      nextCursor: null,
    };
  }

  #governanceRequiresHttp<T>(): Promise<T> {
    return Promise.reject(
      ApiError.of(
        501,
        'Governança P1/P2 requer backend real',
        'Ative VITE_API_MODE=http; o perfil mock não simula contratos de governança.',
      ),
    );
  }

  /* ---- internos ---- */

  /**
   * Simulação do "chefe planeja" (gatilho `[plan]`, ver CHIEF_PLAN_TRIGGER):
   * após o turno, cria demanda + 2 tarefas, move a tarefa A por
   * backlog → ready → development (eventos reais no stream do projeto) e
   * abre uma aprovação de gate pendente ligada a ela. Determinístico:
   * títulos fixos, intervalos fixos, sem aleatoriedade.
   */
  #scheduleChiefPlan(project: Project, content: string): void {
    const baseDelayMs = this.#options.chatChunkDelayMs * (CHAT_REPLY_CHUNKS.length + 2);
    const objective = content.replace(CHIEF_PLAN_TRIGGER, '').trim() || content;
    const stepMs = 700;

    setTimeout(() => {
      void (async () => {
        const demand = await this.create('demands', {
          projectId: project.id,
          title: `Plano: ${objective}`,
          description: `Demanda criada pelo chefe a partir da conversa: "${objective}".`,
          priority: 'high',
        });
        const taskA = await this.create('tasks', {
          projectId: project.id,
          demandId: demand.id,
          title: CHIEF_PLAN_TASK_A,
          priority: 'high',
          instruction: `Detalhar o escopo de "${objective}" em entregáveis verificáveis, com critérios de aceite por item.`,
        });
        await this.create('tasks', {
          projectId: project.id,
          demandId: demand.id,
          title: CHIEF_PLAN_TASK_B,
          priority: 'medium',
          instruction: `Executar a primeira entrega do plano "${objective}" seguindo os critérios de aceite da tarefa anterior.`,
        });

        setTimeout(() => void this.moveTask(taskA.id, { toState: 'ready' }), stepMs);
        setTimeout(() => void this.moveTask(taskA.id, { toState: 'development' }), stepMs * 2);
        setTimeout(
          () =>
            void this.create('approvals', {
              projectId: project.id,
              taskId: taskA.id,
              title: CHIEF_PLAN_APPROVAL_TITLE,
              description: 'Escopo decomposto; aprovar o gate para liberar a execução.',
              requestedByAgentId: project.chiefAgentId,
            }),
          stepMs * 3,
        );
      })();
    }, baseDelayMs);
  }

  #table<K extends ResourceKind>(resource: K): Map<string, ResourceMap[K]> {
    return this.#store[resource];
  }

  /**
   * Linha de log de run-target no stream do projeto (`run.logAppended`
   * com `runId`/`attemptId` nulos — logs de serviço, não de attempt).
   */
  #emitRunLog(target: Pick<RunTarget, 'projectId'>, line: string): void {
    this.#options.realtime?.emit(streams.project(target.projectId), 'run.logAppended', {
      runId: null,
      attemptId: null,
      line,
    });
  }

  /** Registro de auditoria + evento realtime (ações do chefe e afins). */
  #appendAudit(    actorKind: 'user' | 'chief' | 'agent' | 'system',
    actorId: Ulid | null,
    action: string,
    targetType: string,
    targetId: Ulid | null,
    detail: string,
  ): void {
    const auditEvent = {
      id: this.#options.nextId(),
      actorKind,
      actorId,
      action,
      targetType,
      targetId,
      detail,
      occurredAt: this.#options.now(),
    };
    this.#table('audit-events').set(auditEvent.id, auditEvent);
    this.#options.realtime?.emit(streams.global(), 'audit.eventAppended', { auditEvent });
  }

  #require<K extends ResourceKind>(resource: K, id: Ulid): ResourceMap[K] {
    const item = this.#table(resource).get(id);
    if (!item) throw this.#notFound(resource, id);
    return item;
  }

  /** Próximo número de versão do template (máximo + 1). */
  #nextVersionNumber(templateId: Ulid): number {
    return (
      [...this.#table('workflow-versions').values()]
        .filter((version) => version.templateId === templateId)
        .reduce((max, entry) => Math.max(max, entry.version), 0) + 1
    );
  }

  /** Exige versão existente e em rascunho (única fase editável/excluível). */
  #requireDraftVersion(versionId: Ulid): WorkflowVersion {
    const version = this.#require('workflow-versions', versionId);
    if (version.state === 'archived') {
      throw ApiError.of(409, 'Versão arquivada', 'Versões arquivadas não podem ser alteradas.');
    }
    if (version.state !== 'draft') {
      throw ApiError.of(
        409,
        'Versão publicada é imutável',
        'Versões publicadas não podem ser alteradas — crie um novo rascunho (duplicar/nova versão).',
      );
    }
    return version;
  }

  /** Versão "utilizada": vinculada como ativa de um workflow ou de um run. */
  #isVersionUsed(versionId: Ulid): boolean {
    const inWorkflow = [...this.#table('workflows').values()].some(
      (workflow) => workflow.activeVersionId === versionId,
    );
    const inRun = [...this.#table('workflow-runs').values()].some(
      (run) => run.versionId === versionId,
    );
    return inWorkflow || inRun;
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
          archivedAt: null,
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
          priority: i.priority ?? 'medium',
          dueAt: i.dueAt ?? null,
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
          classifications: i.classifications ?? [],
          phaseName: i.phaseName ?? null,
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
      case 'profiles': {
        const i = input as CreateInputMap['profiles'];
        const profile: Profile = {
          id,
          displayName: i.displayName,
          email: i.email ?? null,
          avatarUrl: i.avatarUrl ?? null,
          locale: i.locale,
          createdAt: now,
          lastActiveAt: now,
        };
        // Todo perfil local nasce com settings padrão (tema/idioma do wizard
        // são aplicados em seguida via update de settings).
        const settingsId = this.#options.nextId();
        this.#table('settings').set(settingsId, {
          id: settingsId,
          profileId: profile.id,
          theme: 'system',
          language: i.locale,
          notificationsEnabled: true,
          mutedCategories: [],
          workingDirectory: null,
          unsafeModeAcceptedAt: null,
          updatedAt: now,
        });
        return profile as unknown as ResourceMap[K];
      }
      case 'organizations': {
        const i = input as CreateInputMap['organizations'];
        return {
          id,
          name: i.name,
          slug: i.slug,
          plan: i.plan ?? 'free',
          brand: i.brand ?? { logoUrl: null, primaryColor: null, secondaryColor: null, typography: null },
          defaultWorkflowTemplateIds: [],
          templateKeys: [],
          policies: [],
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
          state: 'active',
          criticality: i.criticality ?? 'medium',
          repositoryUrl: i.repositoryUrl ?? null,
          repositoryProvider: i.repositoryProvider ?? 'other',
          defaultBranch: i.defaultBranch ?? 'main',
          technologies: i.technologies ?? [],
          brand: i.brand ?? { logoUrl: null, primaryColor: null, secondaryColor: null, typography: null },
          memberProfileIds: i.memberProfileIds ?? [this.#options.currentProfileId],
          configVersion: 1,
          configHistory: [],
          chiefAgentId: id, // placeholder: backend vincula o chefe provisionado
          operationMode: 'manual',
          prototyping: { mode: 'autonomousGeneration', waiver: null },
          createdAt: now,
          lastActivityAt: now,
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
      case 'projects': {
        const project = entity as Project;
        // O projeto nasce com `chiefAgentId`, então o Chief precisa existir de
        // fato — as fixtures mantêm essa invariante e a prontidão canônica a
        // verifica (agente ausente = `chief.missing`). Sem isto, um projeto
        // criado pela UI jamais alcançaria `ExecutionReady`.
        // NOTA: o Host real NÃO provisiona este agente hoje; a lacuna está
        // registrada em `docs/frontend/HANDOFF_API.md`.
        this.#provisionChiefAgent(project);
        realtime.emit(streams.global(), 'project.created', { project });
        break;
      }
      case 'prototypes': {
        const prototype = entity as Prototype;
        realtime.emit(streams.project(prototype.projectId), 'prototype.created', { prototype });
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
