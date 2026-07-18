import { z } from 'zod';

import {
  approvalStateSchema,
  attemptStateSchema,
  gateStateSchema,
  instructionAuthorKindSchema,
  operationModeSchema,
  phaseStateSchema,
  prioritySchema,
  taskStateSchema,
  workflowRunStateSchema,
} from './enums';
import { isoDateTimeSchema, progressSchema, ulidSchema } from './primitives';

/**
 * Tarefa técnica — criada pelo chefe, NUNCA por humano.
 * `state` é a coluna do quadro; progresso em três trilhas separadas.
 */
export const taskSchema = z.object({
  id: ulidSchema,
  projectId: ulidSchema,
  demandId: ulidSchema.nullable(),
  title: z.string(),
  state: taskStateSchema,
  priority: prioritySchema,
  assigneeAgentId: ulidSchema.nullable(),
  /** Motivo do bloqueio quando `state === 'blocked'`. */
  blockedReason: z.string().nullable(),
  /** Versão da instrução vigente (`task-instructions`). */
  instructionVersion: z.number().int().positive(),
  progress: progressSchema,
  createdAt: isoDateTimeSchema,
  updatedAt: isoDateTimeSchema,
  dueAt: isoDateTimeSchema.nullable(),
});
export type Task = z.infer<typeof taskSchema>;

/**
 * Instrução de tarefa — IMUTÁVEL e versionada.
 * Correção cria nova versão (sem PUT). `version` começa em 1.
 */
export const taskInstructionSchema = z.object({
  id: ulidSchema,
  taskId: ulidSchema,
  version: z.number().int().positive(),
  body: z.string(),
  authorKind: instructionAuthorKindSchema,
  authorId: ulidSchema.nullable(),
  createdAt: isoDateTimeSchema,
});
export type TaskInstruction = z.infer<typeof taskInstructionSchema>;

/** Tentativa de execução de uma tarefa, com evidências de custo/tokens. */
export const attemptSchema = z.object({
  id: ulidSchema,
  taskId: ulidSchema,
  /** Número sequencial da tentativa dentro da tarefa (1, 2, 3...). */
  number: z.number().int().positive(),
  state: attemptStateSchema,
  agentId: ulidSchema,
  startedAt: isoDateTimeSchema,
  finishedAt: isoDateTimeSchema.nullable(),
  durationMs: z.number().int().nonnegative().nullable(),
  costUsd: z.number().nonnegative(),
  tokensInput: z.number().int().nonnegative(),
  tokensOutput: z.number().int().nonnegative(),
  /** Commits/diffs referenciados como evidência (SHAs ou refs). */
  commitRefs: z.array(z.string()),
  summary: z.string().nullable(),
  /** Motivo da falha quando `state === 'failed'`. */
  failureReason: z.string().nullable(),
});
export type Attempt = z.infer<typeof attemptSchema>;

/** Evento/linha de evidência de uma tentativa (logs, chamadas de ferramenta). */
export const attemptEventSchema = z.object({
  id: ulidSchema,
  attemptId: ulidSchema,
  kind: z.enum(['log', 'toolCall', 'note', 'diff']),
  content: z.string(),
  occurredAt: isoDateTimeSchema,
});
export type AttemptEvent = z.infer<typeof attemptEventSchema>;

export const workflowTemplateSchema = z.object({
  id: ulidSchema,
  name: z.string(),
  description: z.string(),
  currentVersionId: ulidSchema.nullable(),
  createdAt: isoDateTimeSchema,
});
export type WorkflowTemplate = z.infer<typeof workflowTemplateSchema>;

/** Versão publicada (imutável) de um template de workflow. */
export const workflowVersionSchema = z.object({
  id: ulidSchema,
  templateId: ulidSchema,
  version: z.number().int().positive(),
  /** Nomes das fases em ordem. */
  phases: z.array(z.string()),
  /** Nomes dos gates por fase (fase → gates). */
  gatesByPhase: z.record(z.array(z.string())),
  changelog: z.string().nullable(),
  publishedAt: isoDateTimeSchema,
});
export type WorkflowVersion = z.infer<typeof workflowVersionSchema>;

/** Registro de aceite de risco na troca de modo de operação. */
export const riskAcceptanceSchema = z.object({
  mode: operationModeSchema,
  acceptedByProfileId: ulidSchema,
  note: z.string(),
  acceptedAt: isoDateTimeSchema,
});
export type RiskAcceptance = z.infer<typeof riskAcceptanceSchema>;

/** Workflow vinculado a um projeto (versão ativa + modo de operação). */
export const workflowSchema = z.object({
  id: ulidSchema,
  projectId: ulidSchema,
  templateId: ulidSchema,
  activeVersionId: ulidSchema,
  operationMode: operationModeSchema,
  /** No modo semiautônomo: quais gates pausam para aprovação humana. */
  semiautonomousPauseGates: z.array(z.string()),
  riskAcceptances: z.array(riskAcceptanceSchema),
  createdAt: isoDateTimeSchema,
});
export type Workflow = z.infer<typeof workflowSchema>;

export const workflowRunSchema = z.object({
  id: ulidSchema,
  workflowId: ulidSchema,
  versionId: ulidSchema,
  state: workflowRunStateSchema,
  startedAt: isoDateTimeSchema,
  finishedAt: isoDateTimeSchema.nullable(),
});
export type WorkflowRun = z.infer<typeof workflowRunSchema>;

export const phaseSchema = z.object({
  id: ulidSchema,
  runId: ulidSchema,
  name: z.string(),
  order: z.number().int().positive(),
  state: phaseStateSchema,
  startedAt: isoDateTimeSchema.nullable(),
  finishedAt: isoDateTimeSchema.nullable(),
});
export type Phase = z.infer<typeof phaseSchema>;

export const gateSchema = z.object({
  id: ulidSchema,
  phaseId: ulidSchema,
  runId: ulidSchema,
  name: z.string(),
  state: gateStateSchema,
  requiresApproval: z.boolean(),
  decidedByProfileId: ulidSchema.nullable(),
  decidedAt: isoDateTimeSchema.nullable(),
  /** Observação — obrigatória em reprovação. */
  note: z.string().nullable(),
});
export type Gate = z.infer<typeof gateSchema>;

/**
 * Aprovação humana (gate, documento, mudança de modo...).
 * Reprovação EXIGE `resolutionNote`.
 */
export const approvalSchema = z
  .object({
    id: ulidSchema,
    projectId: ulidSchema,
    gateId: ulidSchema.nullable(),
    taskId: ulidSchema.nullable(),
    documentId: ulidSchema.nullable(),
    title: z.string(),
    description: z.string(),
    state: approvalStateSchema,
    requestedByAgentId: ulidSchema,
    requestedAt: isoDateTimeSchema,
    resolvedByProfileId: ulidSchema.nullable(),
    resolvedAt: isoDateTimeSchema.nullable(),
    resolutionNote: z.string().nullable(),
  })
  .refine((a) => a.state !== 'rejected' || (a.resolutionNote?.length ?? 0) > 0, {
    message: 'Reprovação exige observação (resolutionNote).',
    path: ['resolutionNote'],
  });
export type Approval = z.infer<typeof approvalSchema>;
