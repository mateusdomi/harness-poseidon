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
