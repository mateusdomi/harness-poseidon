import { z } from 'zod';

import { isoDateTimeSchema } from './primitives';

const contractIdSchema = z.string().min(1);
const wireIntegerSchema = z.coerce.number().int();

export const governanceReceiptDocumentSchema = z.object({
  documentId: contractIdSchema,
  checksum: z.string().min(1),
  selectionReason: z.string(),
  loadPolicy: z.string(),
  estimatedTokens: wireIntegerSchema,
});
export type GovernanceReceiptDocument = z.infer<typeof governanceReceiptDocumentSchema>;

export const governanceReceiptSchema = z.object({
  projectId: contractIdSchema,
  taskId: contractIdSchema,
  attemptId: contractIdSchema,
  turnId: contractIdSchema,
  agentId: contractIdSchema,
  manifestVersion: z.string().min(1),
  documents: z.array(governanceReceiptDocumentSchema),
  estimatedTokens: wireIntegerSchema,
  actualPromptTokens: wireIntegerSchema.nullable(),
  truncated: z.array(z.string()),
  conflicts: z.array(z.string()),
  cacheHits: wireIntegerSchema,
  provider: z.string().min(1),
  model: z.string().nullable(),
  timestamp: isoDateTimeSchema,
  bundleChecksum: z.string().min(1),
  state: z.string().min(1),
  gateResult: z.string().nullable(),
  version: wireIntegerSchema,
});
export type GovernanceReceipt = z.infer<typeof governanceReceiptSchema>;

export const governanceMetricSchema = z.object({
  projectId: contractIdSchema,
  turnId: contractIdSchema,
  eventId: contractIdSchema,
  kind: z.string().min(1),
  documentId: z.string().nullable(),
  ruleId: z.string().nullable(),
  detailCode: z.string().nullable(),
  tokenCount: wireIntegerSchema.nullable(),
  occurredAt: isoDateTimeSchema,
});
export type GovernanceMetric = z.infer<typeof governanceMetricSchema>;

export const staleDocumentFindingSchema = z.object({
  findingId: contractIdSchema,
  documentId: contractIdSchema,
  kind: z.string().min(1),
  detail: z.string(),
  recommendedTask: z.string(),
  detectedAt: isoDateTimeSchema,
});
export type StaleDocumentFinding = z.infer<typeof staleDocumentFindingSchema>;

export const evaluationTestSchema = z.object({
  name: z.string().min(1),
  passed: z.boolean(),
  evidenceReference: z.string().min(1),
});
export type EvaluationTest = z.infer<typeof evaluationTestSchema>;

export const freshContextEvaluationInputSchema = z.object({
  evaluationId: contractIdSchema,
  projectId: contractIdSchema,
  taskId: contractIdSchema,
  attemptId: contractIdSchema,
  turnId: contractIdSchema,
  actorAgentId: contractIdSchema,
  evaluatorAgentId: contractIdSchema,
  riskTier: z.string().min(1),
  acceptanceCriteria: z.array(z.string().min(1)).min(1),
  diff: z.string().min(1),
  evidence: z.array(z.string().min(1)).min(1),
  testResults: z.array(evaluationTestSchema),
});
export type FreshContextEvaluationInput = z.infer<typeof freshContextEvaluationInputSchema>;

export const evaluationFindingSchema = z.object({
  priority: z.string().min(1),
  confidence: z.coerce.number(),
  evidence: z.string(),
  path: z.string().nullable(),
  range: z.string().nullable(),
  ruleId: z.string().min(1),
  recommendedAction: z.string(),
});
export type EvaluationFinding = z.infer<typeof evaluationFindingSchema>;

export const evaluationResultSchema = z.object({
  schemaVersion: z.string().min(1),
  evaluationId: contractIdSchema,
  verdict: z.string().min(1),
  findings: z.array(evaluationFindingSchema),
  provider: z.string().min(1),
  model: z.string().nullable(),
  readOnly: z.boolean(),
  cleanContext: z.boolean(),
  evaluatedAt: isoDateTimeSchema,
});
export type EvaluationResult = z.infer<typeof evaluationResultSchema>;

export const hashlinePatchInputSchema = z.object({
  turnId: contractIdSchema,
  relativePath: z.string().min(1),
  expectedChecksum: z.string().min(1),
  newContent: z.string(),
});
export type HashlinePatchInput = z.infer<typeof hashlinePatchInputSchema>;

export const hashlinePatchResultSchema = z.object({
  status: z.string().min(1),
  relativePath: z.string().min(1),
  expectedChecksum: z.string().min(1),
  actualChecksum: z.string().min(1),
  appliedChecksum: z.string().nullable(),
  action: z.string().min(1),
});
export type HashlinePatchResult = z.infer<typeof hashlinePatchResultSchema>;

export const patchBenchmarkSchema = z.object({
  strategy: z.string().min(1),
  editSuccesses: wireIntegerSchema,
  staleRejections: wireIntegerSchema,
  retries: wireIntegerSchema,
  estimatedTokens: wireIntegerSchema,
  durationMicroseconds: wireIntegerSchema,
  regressions: wireIntegerSchema,
});
export type PatchBenchmark = z.infer<typeof patchBenchmarkSchema>;

export const agentExecutorSchema = z.object({
  id: contractIdSchema,
  available: z.boolean(),
  enabled: z.boolean(),
  executableName: z.string().nullable(),
  license: z.string(),
  availabilityReason: z.string(),
});
export type AgentExecutor = z.infer<typeof agentExecutorSchema>;

export interface GovernanceReceiptQuery {
  projectId?: string;
  cursor?: string;
  limit?: number;
}

export const learningCandidateTypeSchema = z.enum([
  'rule',
  'skill',
  'persona_refinement',
  'workflow_refinement',
  'tool_routing_recommendation',
  'documentation_correction',
  'provider_model_routing_recommendation',
]);
export type LearningCandidateType = z.infer<typeof learningCandidateTypeSchema>;

export const learningCandidateStateSchema = z.enum([
  'candidate',
  'in_review',
  'awaiting_evaluation',
  'evaluated',
  'shadow',
  'approved',
  'rejected',
  'promoted',
  'rolled_back',
  'deprecated',
]);
export type LearningCandidateState = z.infer<typeof learningCandidateStateSchema>;

export const learningCandidateActionSchema = z.enum([
  'request_review',
  'request_evaluation',
  'complete_evaluation',
  'start_shadow',
  'approve',
  'reject',
  'promote',
  'rollback',
  'deprecate',
]);
export type LearningCandidateAction = z.infer<typeof learningCandidateActionSchema>;

export const learningEvidenceRecordSchema = z.object({
  kind: z.string().min(1),
  reference: z.string().min(1),
  checksum: z.string().min(1),
  summary: z.string(),
});
export type LearningEvidenceRecord = z.infer<typeof learningEvidenceRecordSchema>;

export const learningCandidatePayloadSchema = z.object({
  title: z.string().min(1),
  statement: z.string().nullable(),
  instructions: z.string().nullable(),
  personaId: z.string().nullable(),
  workflowId: z.string().nullable(),
  toolId: z.string().nullable(),
  documentId: z.string().nullable(),
  providerId: z.string().nullable(),
  modelId: z.string().nullable(),
  refinement: z.string().nullable(),
  recommendation: z.string().nullable(),
  correction: z.string().nullable(),
});
export type LearningCandidatePayload = z.infer<typeof learningCandidatePayloadSchema>;

export const learningShadowResultSchema = z.object({
  sampleSize: wireIntegerSchema,
  firstPassSuccessDelta: z.coerce.number(),
  repeatedErrorRateDelta: z.coerce.number(),
  tokenImpact: wireIntegerSchema,
  costPerAcceptedTaskDelta: z.coerce.number(),
  regressions: wireIntegerSchema,
  evidenceReference: z.string().min(1),
});
export type LearningShadowResult = z.infer<typeof learningShadowResultSchema>;

export const learningCandidateSchema = z.object({
  organizationId: contractIdSchema,
  projectId: contractIdSchema,
  candidateId: contractIdSchema,
  type: learningCandidateTypeSchema,
  state: learningCandidateStateSchema,
  fingerprint: z.string().min(1),
  observation: z.string(),
  evidence: z.array(learningEvidenceRecordSchema),
  payload: learningCandidatePayloadSchema,
  actorAgentId: contractIdSchema,
  actorProvider: z.string().min(1),
  actorModel: z.string().nullable(),
  baselineVersion: z.string().min(1),
  proposedVersion: z.string().min(1),
  evaluatorAgentId: z.string().nullable(),
  evaluatorProvider: z.string().nullable(),
  evaluatorModel: z.string().nullable(),
  evaluationVerdict: z.string().nullable(),
  shadowResult: learningShadowResultSchema.nullable(),
  reviewerProfileId: z.string().nullable(),
  decisionNote: z.string().nullable(),
  activeVersion: z.string().nullable(),
  previousVersion: z.string().nullable(),
  createdAt: isoDateTimeSchema,
  updatedAt: isoDateTimeSchema,
  version: wireIntegerSchema,
});
export type LearningCandidate = z.infer<typeof learningCandidateSchema>;

export const learningCandidatePageSchema = z.object({
  items: z.array(learningCandidateSchema),
  nextCursor: z.string().nullable(),
  total: wireIntegerSchema.nonnegative(),
});
export type LearningCandidatePage = z.infer<typeof learningCandidatePageSchema>;

export const learningCandidateComparisonSchema = z.object({
  baselineVersion: z.string().min(1),
  proposedVersion: z.string().min(1),
  proposedPayload: learningCandidatePayloadSchema,
  activeVersion: z.string().nullable(),
  previousVersion: z.string().nullable(),
});
export type LearningCandidateComparison = z.infer<typeof learningCandidateComparisonSchema>;

const historyStateSchema = z.union([learningCandidateStateSchema, wireIntegerSchema]);
const historyActionSchema = z.union([learningCandidateActionSchema, wireIntegerSchema]).nullable();
export const learningCandidateHistoryRecordSchema = z.object({
  eventId: contractIdSchema,
  candidateId: contractIdSchema,
  fromState: historyStateSchema,
  toState: historyStateSchema,
  action: historyActionSchema,
  actorId: contractIdSchema,
  note: z.string().nullable(),
  occurredAt: isoDateTimeSchema,
  candidateVersion: wireIntegerSchema,
});
export type LearningCandidateHistoryRecord = z.infer<typeof learningCandidateHistoryRecordSchema>;

export const learningCandidateMetricsSchema = z.object({
  created: wireIntegerSchema,
  deduplicated: wireIntegerSchema,
  rejected: wireIntegerSchema,
  approved: wireIntegerSchema,
  promoted: wireIntegerSchema,
  rolledBack: wireIntegerSchema,
  averageFirstPassSuccessDelta: z.coerce.number(),
  averageRepeatedErrorRateDelta: z.coerce.number(),
  tokenImpact: wireIntegerSchema,
  averageCostPerAcceptedTaskDelta: z.coerce.number(),
  regressionsAfterPromotion: wireIntegerSchema,
});
export type LearningCandidateMetrics = z.infer<typeof learningCandidateMetricsSchema>;

export const learningTransitionInputSchema = z.object({
  expectedVersion: wireIntegerSchema,
  note: z.string().nullable(),
});
export type LearningTransitionInput = z.infer<typeof learningTransitionInputSchema>;

export const learningEvaluationInputSchema = learningTransitionInputSchema.extend({
  evaluatorAgentId: contractIdSchema,
  evaluatorProvider: z.string().min(1),
  evaluatorModel: z.string().nullable(),
  verdict: z.string().min(1),
});
export type LearningEvaluationInput = z.infer<typeof learningEvaluationInputSchema>;

export const learningShadowInputSchema = learningTransitionInputSchema.extend({
  result: learningShadowResultSchema,
});
export type LearningShadowInput = z.infer<typeof learningShadowInputSchema>;

export const learningDecisionInputSchema = learningTransitionInputSchema.extend({
  approved: z.boolean(),
});
export type LearningDecisionInput = z.infer<typeof learningDecisionInputSchema>;

export interface LearningCandidateQuery {
  organizationId?: string;
  projectId?: string;
  type?: LearningCandidateType;
  state?: LearningCandidateState;
  cursor?: string;
  limit?: number;
}

export type LearningTransition =
  | 'review'
  | 'evaluation-request'
  | 'promotion'
  | 'rollback'
  | 'deprecation';

// ── Fase 5/6/10/12 — operação estatística e auditável do runtime ─────────────────────────────
// Espelham os contratos do Host (`GovernanceRuntimeEndpoints`): recomendações derivadas das
// tentativas gravadas, contenção medida do merge serializado, reconciliação do ledger e busca
// na memória semântica com citações. Nada aqui é inventado no cliente — só validação de forma.

export const performanceAggregateSchema = z.object({
  targetId: z.string(),
  model: z.string().nullable(),
  provider: z.string().nullable(),
  sampleSize: z.number().int(),
  successCount: z.number().int(),
  failureCount: z.number().int(),
  passRate: z.number(),
  compositeScore: z.number(),
  confidenceIntervalLower: z.number(),
  confidenceIntervalUpper: z.number(),
  sampleSizeQualified: z.boolean(),
});
export type PerformanceAggregate = z.infer<typeof performanceAggregateSchema>;

export const evaluationRecommendationSchema = z.object({
  targetId: z.string(),
  model: z.string().nullable(),
  provider: z.string().nullable(),
  action: z.string(),
  score: z.number(),
  confidenceIntervalLower: z.number(),
  confidenceIntervalUpper: z.number(),
  sampleSize: z.number().int(),
  recommendationReason: z.string(),
  generatedAt: z.string(),
});
export type EvaluationRecommendation = z.infer<typeof evaluationRecommendationSchema>;

export const evaluationRecommendationsResponseSchema = z.object({
  projectId: z.string(),
  aggregates: performanceAggregateSchema.array(),
  recommendations: evaluationRecommendationSchema.array(),
});
export type EvaluationRecommendationsResponse = z.infer<
  typeof evaluationRecommendationsResponseSchema
>;

export const mergeContentionSchema = z.object({
  enqueued: z.number().int(),
  serialized: z.number().int(),
  contended: z.number().int(),
  waiting: z.number().int(),
  active: z.number().int(),
  totalWaitMs: z.number(),
  maximumWaitMs: z.number(),
  contentionRatio: z.number(),
});
export type MergeContention = z.infer<typeof mergeContentionSchema>;

export const ledgerReconciliationSchema = z.object({
  tenantId: z.string(),
  totalEntries: z.number().int(),
  isChainValid: z.boolean(),
  tamperedCount: z.number().int(),
  discrepancySequenceNumbers: z.number().int().array(),
  lastValidHash: z.string(),
  reconciledAt: z.string(),
});
export type LedgerReconciliation = z.infer<typeof ledgerReconciliationSchema>;

export const memorySliceSchema = z.object({
  documentId: z.string(),
  documentType: z.string(),
  projectId: z.string(),
  content: z.string(),
  score: z.number(),
  citationReference: z.string(),
  provenance: z.record(z.string(), z.string()),
});
export type MemorySlice = z.infer<typeof memorySliceSchema>;

export const memorySearchResponseSchema = z.object({
  snapshotId: z.string(),
  snapshotHash: z.string(),
  totalTokens: z.number().int(),
  slices: memorySliceSchema.array(),
});
export type MemorySearchResponse = z.infer<typeof memorySearchResponseSchema>;

/**
 * Confiabilidade e produtividade do projeto (B1+B12/F16, modo Técnico).
 *
 * Duas leituras que só valem juntas: onde o sistema erra (distribuição MAST) e quem resolve com
 * quantas rodadas (pass@k por conta+modelo). `subscriptions` acrescenta o custo: uma conta pode
 * acertar muito e consumir desproporcionalmente.
 */
export const subscriptionUsageSchema = z.object({
  accountAlias: z.string(),
  providers: z.string().array(),
  invocations: z.number().int(),
  tasksTouched: z.number().int(),
  successes: z.number().int(),
  totalTokens: z.number().int(),
  exactTokens: z.number().int(),
  estimatedTokens: z.number().int(),
  usageUnavailableInvocations: z.number().int(),
  estimatedCostUsd: z.number(),
  lastInvokedAt: z.string(),
});
export type SubscriptionUsage = z.infer<typeof subscriptionUsageSchema>;

export const capabilityMeasurementSchema = z.object({
  accountAlias: z.string(),
  modelTier: z.string(),
  cardType: z.string(),
  k: z.number().int(),
  tasksObserved: z.number().int(),
  passAt1: z.number(),
  passAtK: z.number(),
  recommendedMaxRounds: z.number().int(),
});
export type CapabilityMeasurement = z.infer<typeof capabilityMeasurementSchema>;

/** B14: o turno da chefe medido por intenção — o laço mais quente, antes não medido. */
export const chiefIntentUsageSchema = z.object({
  intent: z.string(),
  turns: z.number().int(),
  averageDurationMs: z.number(),
  p95DurationMs: z.number().int(),
  /** Turnos em que a rota da intenção descartou ação proposta pelo modelo. */
  turnsWithDroppedActions: z.number().int(),
  averageConfidence: z.number(),
});
export type ChiefIntentUsage = z.infer<typeof chiefIntentUsageSchema>;

export const projectReliabilitySchema = z.object({
  projectId: z.string(),
  classifiedAttempts: z.number().int(),
  subscriptions: subscriptionUsageSchema.array(),
  intents: chiefIntentUsageSchema.array(),
  /** `true` quando o histórico é maior que a amostra lida — a tela precisa DIZER isso. */
  sampleTruncated: z.boolean(),
  failureModesByCategory: z.record(z.string(), z.number().int()),
  advice: z.string(),
  capabilities: capabilityMeasurementSchema.array(),
});
export type ProjectReliability = z.infer<typeof projectReliabilitySchema>;
