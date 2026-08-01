import {
  ApiError,
  problemDetailsSchema,
  type Account,
  type Agent,
  type AgentDefinition,
  type AnalyzeSolicitationInput,
  type AppendTaskInstructionInput,
  type ActivateLicenseInput,
  type BackupHandle,
  type ChatTurnHandle,
  type ClassifyDocumentInput,
  type CreatableResource,
  type CreateAccountInput,
  type CreateAgentDefinitionInput,
  type DuplicateAgentDefinitionInput,
  type CreateInputMap,
  type CreateWorkflowTemplateInput,
  type Diagnostics,
  type DocumentVersion,
  type DrainChiefTasksInput,
  type HandoffChiefInput,
  type License,
  type LinkWorkflowTemplateInput,
  type ListQuery,
  type Model,
  type MoveTaskInput,
  type Page,
  type PhaseObligationProgress,
  type Profile,
  type Project,
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
  type Approval,
  type Document,
  type DesignSystemBundle,
  type PrototypingStage,
  type Solicitation,
  agentExecutorSchema,
  activeConversationSelectionSchema,
  designSystemBundleSchema,
  evaluationRecommendationsResponseSchema,
  evaluationResultSchema,
  ledgerReconciliationSchema,
  memorySearchResponseSchema,
  mergeContentionSchema,
  freshContextEvaluationInputSchema,
  governanceMetricSchema,
  governanceReceiptSchema,
  hashlinePatchInputSchema,
  hashlinePatchResultSchema,
  patchBenchmarkSchema,
  staleDocumentFindingSchema,
  learningCandidateComparisonSchema,
  learningCandidateHistoryRecordSchema,
  learningCandidateMetricsSchema,
  learningCandidatePageSchema,
  learningCandidateSchema,
  learningDecisionInputSchema,
  learningEvaluationInputSchema,
  learningEvidenceRecordSchema,
  learningShadowInputSchema,
  learningTransitionInputSchema,
  type AgentExecutor,
  type EvaluationRecommendationsResponse,
  type ProjectReliability,
  projectReliabilitySchema,
  type EvaluationResult,
  type LedgerReconciliation,
  type MemorySearchResponse,
  type MergeContention,
  type FreshContextEvaluationInput,
  type GovernanceMetric,
  type GovernanceReceipt,
  type GovernanceReceiptQuery,
  type HashlinePatchInput,
  type HashlinePatchResult,
  type PatchBenchmark,
  type StaleDocumentFinding,
  type LearningCandidate,
  type LearningCandidateComparison,
  type LearningCandidateHistoryRecord,
  type LearningCandidateMetrics,
  type LearningCandidatePage,
  type LearningCandidateQuery,
  type LearningDecisionInput,
  type LearningEvaluationInput,
  type LearningEvidenceRecord,
  type LearningShadowInput,
  type LearningTransition,
  type LearningTransitionInput,
  type ProjectReadinessSnapshot,
  type GovernanceDocTree,
  type GovernanceDocContent,
  chatTurnHandleSchema,
  projectReadinessSnapshotSchema,
  phaseObligationProgressSchema,
  prototypingStageSchema,
  governanceDocTreeSchema,
  governanceDocContentSchema,
  agentAccountRosterSchema,
  channelLinkSchema,
  channelMessagePageSchema,
  type AgentAccountRoster,
  type ChannelLink,
  type ChannelMessagePage,
  type CreateChannelLinkInput,
} from '../contracts';
import type { ApiClient } from './api-client';
import {
  API_READ_TIMEOUT_MS,
  API_SLOW_REQUEST_MS,
  publishApiRequestTelemetry,
  safeRequestPath,
} from '../request-observability';

/**
 * Codifica um path relativo de documento (rota catch-all `{**path}`):
 * cada segmento é percent-encoded, mas as barras são preservadas. O backend
 * revalida o allowlist e bloqueia traversal — isto é só higiene de URL.
 */
function encodeDocPath(path: string): string {
  return path
    .split('/')
    .map((segment) => encodeURIComponent(segment))
    .join('/');
}

export interface HttpApiClientOptions {
  /** Base URL do backend (ex.: `https://localhost:5001`). Rotas em `/api/v1`. */
  baseUrl: string;
  /** fetch injetável (testes). Padrão: fetch global com credentials de sessão. */
  fetchFn?: typeof fetch;
  /** Limite explícito somente para leituras; escritas nunca são abortadas pelo cliente. */
  readTimeoutMs?: number;
  /** Janela para sinalizar request anormalmente longo sem cancelá-lo. */
  slowRequestMs?: number;
}

/**
 * Cliente HTTP real (modo `VITE_API_MODE=http`): fetch, base `/api/v1`,
 * paginação por cursor, erros `application/problem+json` (RFC 7807) e
 * sessão local via cookie (`credentials: 'include'`, preparado p/ OIDC).
 */
export class HttpApiClient implements ApiClient {
  readonly #baseUrl: string;
  readonly #fetch: typeof fetch;
  readonly #readTimeoutMs: number;
  readonly #slowRequestMs: number;

  constructor(options: HttpApiClientOptions) {
    this.#baseUrl = `${options.baseUrl.replace(/\/$/, '')}/api/v1`;
    this.#fetch = options.fetchFn ?? ((input, init) => fetch(input, init));
    this.#readTimeoutMs = options.readTimeoutMs ?? API_READ_TIMEOUT_MS;
    this.#slowRequestMs = options.slowRequestMs ?? API_SLOW_REQUEST_MS;
  }

  list<K extends ResourceKind>(resource: K, query?: ListQuery): Promise<Page<ResourceMap[K]>> {
    const params = new URLSearchParams();
    if (query?.cursor) params.set('cursor', query.cursor);
    if (query?.limit) params.set('limit', String(query.limit));
    for (const [key, value] of Object.entries(query?.filter ?? {})) {
      if (value !== undefined) params.set(key, String(value));
    }
    const qs = params.toString();
    return this.#request('GET', `/${resource}${qs ? `?${qs}` : ''}`);
  }

  get<K extends ResourceKind>(resource: K, id: Ulid): Promise<ResourceMap[K]> {
    return this.#request('GET', `/${resource}/${id}`);
  }

  create<K extends CreatableResource>(
    resource: K,
    input: CreateInputMap[K],
  ): Promise<ResourceMap[K]> {
    return this.#request('POST', `/${resource}`, input);
  }

  update<K extends UpdatableResource>(
    resource: K,
    id: Ulid,
    input: UpdateInputMap[K],
  ): Promise<ResourceMap[K]> {
    return this.#request('PATCH', `/${resource}/${id}`, input);
  }

  async remove(resource: RemovableResource, id: Ulid): Promise<void> {
    await this.#request('DELETE', `/${resource}/${id}`);
  }

  getCurrentProfile(): Promise<Profile> {
    return this.#request('GET', '/profiles/current');
  }

  async recallActiveConversation(projectId: Ulid): Promise<Ulid | null> {
    const response = await this.#request<unknown>(
      'GET',
      `/projects/${projectId}/conversations/active`,
    );
    if (response === undefined) return null;
    return activeConversationSelectionSchema.parse(response).conversationId;
  }

  async rememberActiveConversation(projectId: Ulid, conversationId: Ulid): Promise<void> {
    await this.#request('PUT', `/projects/${projectId}/conversations/active`, {
      conversationId,
    });
  }

  uploadProjectLogo(projectId: Ulid, file: File): Promise<Project> {
    const form = new FormData();
    form.append('file', file);
    return this.#request('POST', `/projects/${projectId}/logo`, form);
  }

  async getPrototypingStage(projectId: Ulid): Promise<PrototypingStage> {
    const response = await this.#request<unknown>(
      'GET',
      `/projects/${projectId}/prototyping-stage`,
    );
    return prototypingStageSchema.parse(response);
  }

  async uploadDesignSystemBundle(
    projectId: Ulid,
    file: File,
    title?: string,
  ): Promise<DesignSystemBundle> {
    const form = new FormData();
    form.append('file', file);
    if (title?.trim()) form.append('title', title.trim());
    const response = await this.#request<unknown>(
      'POST',
      `/projects/${projectId}/design-system-bundle`,
      form,
    );
    return designSystemBundleSchema.parse(response);
  }

  async uploadSolicitationAttachment(solicitationId: Ulid, file: File): Promise<void> {
    const form = new FormData();
    form.append('file', file);
    await this.#request('POST', `/solicitations/${solicitationId}/attachments/`, form);
  }

  moveTask(taskId: Ulid, input: MoveTaskInput): Promise<Task> {
    return this.#request('POST', `/tasks/${taskId}/moves`, input);
  }

  setTaskPriority(taskId: Ulid, input: SetTaskPriorityInput): Promise<Task> {
    return this.#request('POST', `/tasks/${taskId}/priority`, input);
  }

  archiveTask(taskId: Ulid): Promise<Task> {
    return this.#request('POST', `/tasks/${taskId}/archive`);
  }

  unarchiveTask(taskId: Ulid): Promise<Task> {
    return this.#request('POST', `/tasks/${taskId}/unarchive`);
  }

  appendTaskInstruction(taskId: Ulid, input: AppendTaskInstructionInput): Promise<TaskInstruction> {
    return this.#request('POST', `/tasks/${taskId}/instructions`, input);
  }

  transitionSolicitation(id: Ulid, input: TransitionSolicitationInput): Promise<Solicitation> {
    return this.#request('POST', `/solicitations/${id}/transitions`, input);
  }

  resolveApproval(id: Ulid, input: ResolveApprovalInput): Promise<Approval> {
    return this.#request('POST', `/approvals/${id}/resolution`, input);
  }

  transitionDocument(id: Ulid, input: TransitionDocumentInput): Promise<Document> {
    return this.#request('POST', `/documents/${id}/transitions`, input);
  }

  saveDocumentVersion(id: Ulid, input: SaveDocumentVersionInput): Promise<DocumentVersion> {
    return this.#request('POST', `/documents/${id}/versions`, input);
  }

  classifyDocument(id: Ulid, input: ClassifyDocumentInput): Promise<Document> {
    return this.#request('POST', `/documents/${id}/classification`, input);
  }

  publishWorkflowVersion(
    templateId: Ulid,
    input: PublishWorkflowVersionInput,
  ): Promise<WorkflowVersion> {
    return this.#request('POST', `/workflow-templates/${templateId}/versions`, input);
  }

  /* ---- gestão de templates de workflow (FR-4) ---- */

  createWorkflowTemplate(input: CreateWorkflowTemplateInput): Promise<WorkflowTemplate> {
    return this.#request('POST', '/workflow-templates', input);
  }

  createWorkflowDraftVersion(
    templateId: Ulid,
    input?: WorkflowDraftInput,
  ): Promise<WorkflowVersion> {
    return this.#request('POST', `/workflow-templates/${templateId}/drafts`, input ?? {});
  }

  updateWorkflowDraftVersion(versionId: Ulid, input: WorkflowDraftInput): Promise<WorkflowVersion> {
    return this.#request('PATCH', `/workflow-versions/${versionId}`, input);
  }

  publishWorkflowDraft(
    versionId: Ulid,
    input?: PublishWorkflowDraftInput,
  ): Promise<WorkflowVersion> {
    return this.#request('POST', `/workflow-versions/${versionId}/publish`, input ?? {});
  }

  archiveWorkflowTemplate(templateId: Ulid): Promise<WorkflowTemplate> {
    return this.#request('POST', `/workflow-templates/${templateId}/archive`);
  }

  archiveWorkflowVersion(versionId: Ulid): Promise<WorkflowVersion> {
    return this.#request('POST', `/workflow-versions/${versionId}/archive`);
  }

  async deleteWorkflowDraftVersion(versionId: Ulid): Promise<void> {
    await this.#request('DELETE', `/workflow-versions/${versionId}`);
  }

  async deleteWorkflowTemplate(templateId: Ulid): Promise<void> {
    await this.#request('DELETE', `/workflow-templates/${templateId}`);
  }

  duplicateWorkflowTemplate(templateId: Ulid): Promise<WorkflowTemplate> {
    return this.#request('POST', `/workflow-templates/${templateId}/duplicate`);
  }

  duplicateWorkflowVersion(versionId: Ulid): Promise<WorkflowVersion> {
    return this.#request('POST', `/workflow-versions/${versionId}/duplicate`);
  }

  linkWorkflowTemplate(input: LinkWorkflowTemplateInput): Promise<Workflow> {
    return this.#request('POST', `/projects/${input.projectId}/workflow`, {
      templateId: input.templateId,
      versionId: input.versionId,
    });
  }

  setWorkflowOperationMode(workflowId: Ulid, input: SetOperationModeInput): Promise<Workflow> {
    return this.#request('POST', `/workflows/${workflowId}/operation-mode`, input);
  }

  markNotificationsRead(ids: Ulid[]): Promise<number> {
    return this.#request('POST', '/notifications/read', { ids });
  }

  muteNotifications(ids: Ulid[]): Promise<number> {
    return this.#request('POST', '/notifications/mute', { ids });
  }

  async startChatTurn(conversationId: Ulid, input: StartChatTurnInput): Promise<ChatTurnHandle> {
    const response = await this.#request<unknown>(
      'POST',
      `/conversations/${conversationId}/turns`,
      input,
    );
    return chatTurnHandleSchema.parse(response);
  }

  pauseChief(projectId: Ulid): Promise<Project> {
    return this.#request('POST', `/projects/${projectId}/chief/pause`);
  }

  resumeChief(projectId: Ulid): Promise<Project> {
    return this.#request('POST', `/projects/${projectId}/chief/resume`);
  }

  handoffChief(projectId: Ulid, input: HandoffChiefInput): Promise<Agent> {
    return this.#request('POST', `/projects/${projectId}/chief/handoff`, input);
  }

  drainChiefTasks(projectId: Ulid, input: DrainChiefTasksInput): Promise<number> {
    return this.#request('POST', `/projects/${projectId}/chief/drain`, input);
  }

  startRunTarget(runTargetId: Ulid): Promise<RunTarget> {
    return this.#request('POST', `/run-targets/${runTargetId}/start`);
  }

  stopRunTarget(runTargetId: Ulid): Promise<RunTarget> {
    return this.#request('POST', `/run-targets/${runTargetId}/stop`);
  }

  restartRunTarget(runTargetId: Ulid): Promise<RunTarget> {
    return this.#request('POST', `/run-targets/${runTargetId}/restart`);
  }

  cleanupRunEnvironment(projectId: Ulid): Promise<number> {
    return this.#request('POST', `/projects/${projectId}/run-environment/cleanup`);
  }

  syncProviderCatalog(providerId: Ulid): Promise<Model[]> {
    return this.#request('POST', `/providers/${providerId}/sync`);
  }

  /* ---- contas de provider (FR-5) ---- */

  createAccount(input: CreateAccountInput): Promise<Account> {
    return this.#request('POST', '/accounts', input);
  }

  updateAccount(id: Ulid, input: UpdateAccountInput): Promise<Account> {
    return this.#request('PATCH', `/accounts/${id}`, input);
  }

  enableAccount(id: Ulid): Promise<Account> {
    return this.#request('PATCH', `/accounts/${id}`, { state: 'active' });
  }

  disableAccount(id: Ulid): Promise<Account> {
    return this.#request('PATCH', `/accounts/${id}`, { state: 'disabled' });
  }

  async deleteAccount(id: Ulid): Promise<void> {
    await this.#request('DELETE', `/accounts/${id}`);
  }

  /* ---- definições de agente (FR-5) ----
   * O adapter converte os campos editoriais do refinamento para o contrato
   * V3 publicado pelo backend (arrays semânticos + expectedVersion).
   */

  createAgentDefinition(input: CreateAgentDefinitionInput): Promise<AgentDefinition> {
    return this.#request('POST', '/agent-definitions', this.#agentDefinitionWriteBody(input));
  }

  updateAgentDefinition(id: Ulid, input: UpdateAgentDefinitionInput): Promise<AgentDefinition> {
    return this.#request(
      'PATCH',
      `/agent-definitions/${id}`,
      this.#agentDefinitionWriteBody(input),
    );
  }

  duplicateAgentDefinition(
    id: Ulid,
    input?: DuplicateAgentDefinitionInput,
  ): Promise<AgentDefinition> {
    if (!input) {
      return Promise.reject(
        ApiError.of(400, 'Dados da cópia ausentes', 'Informe chave e nome para duplicar.'),
      );
    }
    return this.#request('POST', `/agent-definitions/${id}/duplicate`, input);
  }

  enableAgentDefinition(id: Ulid): Promise<AgentDefinition> {
    return this.#request('POST', `/agent-definitions/${id}/enable`);
  }

  disableAgentDefinition(id: Ulid): Promise<AgentDefinition> {
    return this.#request('POST', `/agent-definitions/${id}/disable`);
  }

  archiveAgentDefinition(id: Ulid): Promise<AgentDefinition> {
    return this.#request('POST', `/agent-definitions/${id}/archive`);
  }

  async deleteAgentDefinition(id: Ulid): Promise<void> {
    await this.#request('DELETE', `/agent-definitions/${id}`);
  }

  #agentDefinitionWriteBody(input: UpdateAgentDefinitionInput) {
    const lines = (value: string | null | undefined): string[] =>
      (value ?? '')
        .split('\n')
        .map((line) => line.trim())
        .filter(Boolean);
    return {
      key: input.key ?? '',
      name: input.name ?? '',
      role: input.role ?? 'specialist',
      specialty: input.specialty ?? null,
      description: input.description ?? '',
      defaultModelId: input.defaultModelId ?? null,
      skillIds: input.skillIds ?? [],
      toolIds: input.toolIds ?? [],
      persona: input.persona ?? null,
      mission: input.mission ?? null,
      operatingPrinciples: lines(input.instructions),
      deliverables: lines(input.responsibilities),
      qualityCriteria: lines(input.bestPractices),
      communicationStyle: null,
      limitations: lines(input.restrictions),
      expectedVersion: input.expectedVersion ?? 0,
    };
  }

  analyzeSolicitation(input: AnalyzeSolicitationInput): Promise<SolicitationAnalysis> {
    return this.#request('POST', '/solicitations/analyze', input);
  }

  activateLicense(input: ActivateLicenseInput): Promise<License> {
    return this.#request('POST', '/licenses/activation', input);
  }

  createBackup(): Promise<BackupHandle> {
    return this.#request('POST', '/backups');
  }

  async restoreBackup(backupId: Ulid): Promise<void> {
    await this.#request('POST', `/backups/${backupId}/restore`);
  }

  getDiagnostics(): Promise<Diagnostics> {
    return this.#request('GET', '/diagnostics');
  }

  async getProjectReadiness(projectId: Ulid): Promise<ProjectReadinessSnapshot> {
    const response = await this.#request<unknown>('GET', `/projects/${projectId}/readiness`);
    // Valida na fronteira: prontidão dirige bloqueio de execução na UI.
    return projectReadinessSnapshotSchema.parse(response);
  }

  async getPhaseObligationProgress(
    runId: Ulid,
    phaseKey: string,
  ): Promise<PhaseObligationProgress> {
    const response = await this.#request<unknown>(
      'GET',
      `/workflow-runs/${encodeURIComponent(runId)}/phases/${encodeURIComponent(phaseKey)}/progress`,
    );
    return phaseObligationProgressSchema.parse(response);
  }

  async listGovernanceReceipts(query?: GovernanceReceiptQuery): Promise<GovernanceReceipt[]> {
    const params = new URLSearchParams();
    if (query?.projectId) params.set('projectId', query.projectId);
    if (query?.cursor) params.set('cursor', query.cursor);
    if (query?.limit !== undefined) params.set('limit', String(query.limit));
    const suffix = params.size > 0 ? `?${params.toString()}` : '';
    const response = await this.#request<unknown>('GET', `/governance-runtime/receipts${suffix}`);
    return governanceReceiptSchema.array().parse(response);
  }

  async getGovernanceReceipt(turnId: string): Promise<GovernanceReceipt> {
    const response = await this.#request<unknown>(
      'GET',
      `/governance-runtime/receipts/${encodeURIComponent(turnId)}`,
    );
    return governanceReceiptSchema.parse(response);
  }

  async listGovernanceMetrics(turnId: string): Promise<GovernanceMetric[]> {
    const response = await this.#request<unknown>(
      'GET',
      `/governance-runtime/receipts/${encodeURIComponent(turnId)}/metrics`,
    );
    return governanceMetricSchema.array().parse(response);
  }

  async createFreshContextEvaluation(
    input: FreshContextEvaluationInput,
  ): Promise<EvaluationResult> {
    const body = freshContextEvaluationInputSchema.parse(input);
    const response = await this.#request<unknown>('POST', '/governance-runtime/evaluations', body);
    return evaluationResultSchema.parse(response);
  }

  async listStaleDocumentFindings(): Promise<StaleDocumentFinding[]> {
    const response = await this.#request<unknown>('GET', '/governance-runtime/stale-doc-findings');
    return staleDocumentFindingSchema.array().parse(response);
  }

  async applyHashlinePatch(
    projectId: string,
    input: HashlinePatchInput,
  ): Promise<HashlinePatchResult> {
    const body = hashlinePatchInputSchema.parse(input);
    const response = await this.#request<unknown>(
      'POST',
      `/governance-runtime/projects/${encodeURIComponent(projectId)}/hashline-patches`,
      body,
    );
    return hashlinePatchResultSchema.parse(response);
  }

  async listHashlineBenchmark(): Promise<PatchBenchmark[]> {
    const response = await this.#request<unknown>('GET', '/governance-runtime/hashline-benchmark');
    return patchBenchmarkSchema.array().parse(response);
  }

  async listAgentExecutors(): Promise<AgentExecutor[]> {
    const response = await this.#request<unknown>('GET', '/governance-runtime/executors');
    return agentExecutorSchema.array().parse(response);
  }

  async getProjectReliability(projectId: string, k?: number): Promise<ProjectReliability> {
    const query = k === undefined ? '' : `?k=${k}`;
    const response = await this.#request<unknown>(
      'GET',
      `/projects/${projectId}/reliability${query}`,
    );
    return projectReliabilitySchema.parse(response);
  }

  async listEvaluationRecommendations(
    projectId: string,
    minSampleSize?: number,
  ): Promise<EvaluationRecommendationsResponse> {
    const params = new URLSearchParams({ projectId });
    if (minSampleSize !== undefined) params.set('minSampleSize', String(minSampleSize));
    const response = await this.#request<unknown>(
      'GET',
      `/governance-runtime/evaluation-recommendations?${params.toString()}`,
    );
    return evaluationRecommendationsResponseSchema.parse(response);
  }

  async getMergeContention(): Promise<MergeContention> {
    const response = await this.#request<unknown>('GET', '/governance-runtime/merge-contention');
    return mergeContentionSchema.parse(response);
  }

  async reconcileLedger(): Promise<LedgerReconciliation> {
    const response = await this.#request<unknown>(
      'GET',
      '/governance-runtime/ledger-reconciliation',
    );
    return ledgerReconciliationSchema.parse(response);
  }

  async searchMemory(
    query: string,
    projectId?: string,
    topK?: number,
  ): Promise<MemorySearchResponse> {
    const params = new URLSearchParams({ query });
    if (projectId) params.set('projectId', projectId);
    if (topK !== undefined) params.set('topK', String(topK));
    const response = await this.#request<unknown>(
      'GET',
      `/governance-runtime/memory-search?${params.toString()}`,
    );
    return memorySearchResponseSchema.parse(response);
  }

  async listLearningCandidates(query?: LearningCandidateQuery): Promise<LearningCandidatePage> {
    const params = new URLSearchParams();
    if (query?.organizationId) params.set('organizationId', query.organizationId);
    if (query?.projectId) params.set('projectId', query.projectId);
    if (query?.type) params.set('type', query.type);
    if (query?.state) params.set('state', query.state);
    if (query?.cursor) params.set('cursor', query.cursor);
    if (query?.limit !== undefined) params.set('limit', String(query.limit));
    const suffix = params.size > 0 ? `?${params.toString()}` : '';
    const response = await this.#request<unknown>(
      'GET',
      `/governance-runtime/learning-candidates${suffix}`,
    );
    return learningCandidatePageSchema.parse(response);
  }

  async getLearningCandidate(candidateId: string): Promise<LearningCandidate> {
    const response = await this.#request<unknown>(
      'GET',
      `/governance-runtime/learning-candidates/${encodeURIComponent(candidateId)}`,
    );
    return learningCandidateSchema.parse(response);
  }

  async listLearningCandidateEvidence(candidateId: string): Promise<LearningEvidenceRecord[]> {
    const response = await this.#request<unknown>(
      'GET',
      `/governance-runtime/learning-candidates/${encodeURIComponent(candidateId)}/evidence`,
    );
    return learningEvidenceRecordSchema.array().parse(response);
  }

  async compareLearningCandidate(candidateId: string): Promise<LearningCandidateComparison> {
    const response = await this.#request<unknown>(
      'GET',
      `/governance-runtime/learning-candidates/${encodeURIComponent(candidateId)}/compare`,
    );
    return learningCandidateComparisonSchema.parse(response);
  }

  async listLearningCandidateHistory(
    candidateId: string,
  ): Promise<LearningCandidateHistoryRecord[]> {
    const response = await this.#request<unknown>(
      'GET',
      `/governance-runtime/learning-candidates/${encodeURIComponent(candidateId)}/history`,
    );
    return learningCandidateHistoryRecordSchema.array().parse(response);
  }

  async getLearningCandidateMetrics(
    query?: Pick<LearningCandidateQuery, 'organizationId' | 'projectId'>,
  ): Promise<LearningCandidateMetrics> {
    const params = new URLSearchParams();
    if (query?.organizationId) params.set('organizationId', query.organizationId);
    if (query?.projectId) params.set('projectId', query.projectId);
    const suffix = params.size > 0 ? `?${params.toString()}` : '';
    const response = await this.#request<unknown>(
      'GET',
      `/governance-runtime/learning-candidates/metrics${suffix}`,
    );
    return learningCandidateMetricsSchema.parse(response);
  }

  async transitionLearningCandidate(
    candidateId: string,
    transition: LearningTransition,
    input: LearningTransitionInput,
  ): Promise<LearningCandidate> {
    return this.#learningMutation(
      candidateId,
      transition,
      learningTransitionInputSchema.parse(input),
    );
  }

  async evaluateLearningCandidate(
    candidateId: string,
    input: LearningEvaluationInput,
  ): Promise<LearningCandidate> {
    return this.#learningMutation(
      candidateId,
      'evaluations',
      learningEvaluationInputSchema.parse(input),
    );
  }

  async shadowLearningCandidate(
    candidateId: string,
    input: LearningShadowInput,
  ): Promise<LearningCandidate> {
    return this.#learningMutation(candidateId, 'shadow', learningShadowInputSchema.parse(input));
  }

  async decideLearningCandidate(
    candidateId: string,
    input: LearningDecisionInput,
  ): Promise<LearningCandidate> {
    return this.#learningMutation(
      candidateId,
      'decision',
      learningDecisionInputSchema.parse(input),
    );
  }

  async listAgentAccounts(): Promise<AgentAccountRoster[]> {
    const response = await this.#request<{ accounts?: unknown }>('GET', '/agent-accounts');
    return agentAccountRosterSchema.array().parse(response?.accounts ?? []);
  }

  async listChannelLinks(): Promise<ChannelLink[]> {
    const response = await this.#request<{ items?: unknown }>('GET', '/channels/links');
    return channelLinkSchema.array().parse(response?.items ?? []);
  }

  async createChannelLink(input: CreateChannelLinkInput): Promise<ChannelLink> {
    const response = await this.#request<unknown>('POST', '/channels/links', {
      kind: input.kind,
      externalIdentity: input.externalIdentity.trim(),
      projectId: input.projectId,
      conversationId: input.conversationId,
    });
    return channelLinkSchema.parse(response);
  }

  async listChannelMessages(
    linkId: string,
    query?: { afterMessageId?: string; limit?: number },
  ): Promise<ChannelMessagePage> {
    const params = new URLSearchParams();
    if (query?.afterMessageId) params.set('afterMessageId', query.afterMessageId);
    if (query?.limit !== undefined) params.set('limit', String(query.limit));
    const qs = params.toString();
    const response = await this.#request<unknown>(
      'GET',
      `/channels/links/${encodeURIComponent(linkId)}/messages${qs ? `?${qs}` : ''}`,
    );
    return channelMessagePageSchema.parse(response);
  }

  async #learningMutation(
    candidateId: string,
    action: string,
    body: unknown,
  ): Promise<LearningCandidate> {
    const key = `ui-${action}-${globalThis.crypto?.randomUUID?.() ?? Date.now()}`;
    const response = await this.#request<unknown>(
      'POST',
      `/governance-runtime/learning-candidates/${encodeURIComponent(candidateId)}/${action}`,
      body,
      { 'Idempotency-Key': key },
    );
    return learningCandidateSchema.parse(response);
  }

  async listGovernanceDocs(): Promise<GovernanceDocTree> {
    const response = await this.#request<unknown>('GET', '/governance-docs');
    return governanceDocTreeSchema.parse(response);
  }

  async readGovernanceDoc(path: string): Promise<GovernanceDocContent> {
    const response = await this.#request<unknown>('GET', `/governance-docs/${encodeDocPath(path)}`);
    return governanceDocContentSchema.parse(response);
  }

  async saveGovernanceDoc(path: string, content: string): Promise<GovernanceDocContent> {
    const response = await this.#request<unknown>(
      'PUT',
      `/governance-docs/${encodeDocPath(path)}`,
      { content },
    );
    return governanceDocContentSchema.parse(response);
  }

  async deleteGovernanceDoc(path: string): Promise<void> {
    await this.#request<unknown>('DELETE', `/governance-docs/${encodeDocPath(path)}`);
  }

  async #request<T>(
    method: string,
    path: string,
    body?: unknown,
    headers?: Record<string, string>,
  ): Promise<T> {
    const url = `${this.#baseUrl}${path}`;
    const requestId = globalThis.crypto?.randomUUID?.() ?? `request-${Date.now()}-${Math.random()}`;
    const requestPath = safeRequestPath(url);
    const startedAt = performance.now();
    const controller = method === 'GET' ? new AbortController() : null;
    let status: number | undefined;
    publishApiRequestTelemetry({
      requestId,
      method,
      path: requestPath,
      phase: 'started',
      durationMs: 0,
    });
    const slowTimer = setTimeout(
      () =>
        publishApiRequestTelemetry({
          requestId,
          method,
          path: requestPath,
          phase: 'slow',
          durationMs: performance.now() - startedAt,
        }),
      this.#slowRequestMs,
    );
    const timeoutTimer =
      controller === null ? null : setTimeout(() => controller.abort(), this.#readTimeoutMs);

    try {
      const multipart = typeof FormData !== 'undefined' && body instanceof FormData;
      const response = await this.#fetch(url, {
        method,
        credentials: 'include',
        headers:
          body !== undefined && !multipart
            ? { 'Content-Type': 'application/json', ...headers }
            : headers,
        body: body !== undefined ? (multipart ? body : JSON.stringify(body)) : undefined,
        signal: controller?.signal,
      });
      status = response.status;
      if (!response.ok) throw await this.#toApiError(response);
      if (response.status === 204) return undefined as T;
      try {
        return (await response.json()) as T;
      } catch {
        status = 502;
        throw ApiError.of(502, 'Resposta inválida da API', 'A API retornou JSON inválido.');
      }
    } catch (error) {
      if (controller?.signal.aborted) {
        status = 504;
        throw ApiError.of(
          504,
          'Tempo limite da API',
          'A leitura excedeu o limite seguro e pode ser tentada novamente.',
        );
      }
      if (error instanceof ApiError) throw error;
      status = 503;
      throw ApiError.of(
        503,
        'API indisponível',
        error instanceof Error ? error.message : undefined,
      );
    } finally {
      clearTimeout(slowTimer);
      if (timeoutTimer !== null) clearTimeout(timeoutTimer);
      publishApiRequestTelemetry({
        requestId,
        method,
        path: requestPath,
        phase: 'settled',
        durationMs: performance.now() - startedAt,
        status,
      });
    }
  }

  async #toApiError(response: Response): Promise<ApiError> {
    const contentType = response.headers.get('content-type') ?? '';
    if (contentType.includes('application/problem+json')) {
      const parsed = problemDetailsSchema.safeParse(await response.json());
      if (parsed.success) return new ApiError(parsed.data);
    }
    return ApiError.of(response.status, response.statusText || 'Erro na API');
  }
}
