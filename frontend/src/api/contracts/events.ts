import { z } from 'zod';

import {
  agentStateSchema,
  approvalStateSchema,
  chiefTurnStateSchema,
  componentStateSchema,
  documentStateSchema,
  gateStateSchema,
  prototypeStateSchema,
  taskStateSchema,
} from './enums';
import { auditEventSchema, notificationSchema } from './system';
import { demandSchema, messageSchema, projectSchema } from './core';
import { prototypeSchema } from './content';
import { isoDateTimeSchema, progressSchema, progressTrackSchema, ulidSchema } from './primitives';
import { approvalSchema, attemptSchema, taskSchema } from './delivery';

/**
 * Envelope de evento de tempo real — hub único `/hubs/events`.
 * `sequence` é crescente POR STREAM: usar para dedupe e detecção de
 * lacuna (lacuna → pedir snapshot do stream).
 */
const envelopeBase = {
  stream: z.string(),
  sequence: z.number().int().positive(),
  occurredAt: isoDateTimeSchema,
};

/* ---- payloads (um por tipo de evento do catálogo) ---- */

export const chatTurnStartedPayloadSchema = z.object({
  conversationId: ulidSchema,
  turnId: ulidSchema,
  agentId: ulidSchema,
});
export const chatTurnChunkPayloadSchema = z.object({
  conversationId: ulidSchema,
  turnId: ulidSchema,
  index: z.number().int().nonnegative(),
  text: z.string(),
});
export const chatTurnCompletedPayloadSchema = z.object({
  conversationId: ulidSchema,
  turnId: ulidSchema,
  messageId: ulidSchema,
  finishReason: z.enum(['stop', 'cancelled', 'error']),
});
export const messageAppendedPayloadSchema = z.object({ message: messageSchema });
export const demandCreatedPayloadSchema = z.object({ demand: demandSchema });
export const taskCreatedPayloadSchema = z.object({ task: taskSchema });
export const taskStateChangedPayloadSchema = z.object({
  taskId: ulidSchema,
  from: taskStateSchema,
  to: taskStateSchema,
  changedByKind: z.enum(['user', 'chief', 'agent', 'system']),
  note: z.string().nullable(),
});
export const attemptStartedPayloadSchema = z.object({ attempt: attemptSchema });
export const attemptHeartbeatPayloadSchema = z.object({
  attemptId: ulidSchema,
  taskId: ulidSchema,
  elapsedMs: z.number().int().nonnegative(),
  tokensInput: z.number().int().nonnegative(),
  tokensOutput: z.number().int().nonnegative(),
  costUsd: z.number().nonnegative(),
});
export const attemptCompletedPayloadSchema = z.object({
  attemptId: ulidSchema,
  taskId: ulidSchema,
  durationMs: z.number().int().nonnegative(),
  tokensInput: z.number().int().nonnegative(),
  tokensOutput: z.number().int().nonnegative(),
  costUsd: z.number().nonnegative(),
  commitRefs: z.array(z.string()),
  summary: z.string().nullable(),
});
export const attemptFailedPayloadSchema = z.object({
  attemptId: ulidSchema,
  taskId: ulidSchema,
  reason: z.string(),
  durationMs: z.number().int().nonnegative(),
  costUsd: z.number().nonnegative(),
});
export const gateChangedPayloadSchema = z.object({
  gateId: ulidSchema,
  runId: ulidSchema,
  from: gateStateSchema,
  to: gateStateSchema,
  decidedByProfileId: ulidSchema.nullable(),
  note: z.string().nullable(),
});
export const approvalRequestedPayloadSchema = z.object({ approval: approvalSchema });
export const approvalResolvedPayloadSchema = z.object({
  approvalId: ulidSchema,
  state: approvalStateSchema,
  resolvedByProfileId: ulidSchema,
  note: z.string().nullable(),
});
export const documentStateChangedPayloadSchema = z.object({
  documentId: ulidSchema,
  from: documentStateSchema,
  to: documentStateSchema,
});
export const prototypeStateChangedPayloadSchema = z.object({
  prototypeId: ulidSchema,
  from: prototypeStateSchema,
  to: prototypeStateSchema,
});
export const workflowVersionPublishedPayloadSchema = z.object({
  templateId: ulidSchema,
  versionId: ulidSchema,
  version: z.number().int().positive(),
});
export const notificationCreatedPayloadSchema = z.object({ notification: notificationSchema });
export const agentStatusChangedPayloadSchema = z.object({
  agentId: ulidSchema,
  from: agentStateSchema,
  to: agentStateSchema,
  currentTaskId: ulidSchema.nullable(),
});
export const toolStatusChangedPayloadSchema = z.object({
  toolId: ulidSchema,
  from: componentStateSchema,
  to: componentStateSchema,
});
export const auditEventAppendedPayloadSchema = z.object({ auditEvent: auditEventSchema });
export const runLogAppendedPayloadSchema = z.object({
  runId: ulidSchema.nullable(),
  attemptId: ulidSchema.nullable(),
  line: z.string(),
});
export const progressUpdatedPayloadSchema = z.object({
  taskId: ulidSchema,
  track: progressTrackSchema,
  value: z.number().min(0).max(100),
  progress: progressSchema,
});
export const quotaUpdatedPayloadSchema = z.object({
  accountId: ulidSchema.nullable(),
  budgetId: ulidSchema.nullable(),
  usedUsd: z.number().nonnegative(),
  limitUsd: z.number().nonnegative().nullable(),
});
export const chiefTurnStateChangedPayloadSchema = z.object({
  conversationId: ulidSchema,
  turnId: ulidSchema.nullable(),
  state: chiefTurnStateSchema,
});

/* ---- eventos do catálogo canônico docs/contracts/events.json (FR-5) ----
 * Nomes publicados pelo backend; payloads propostos pelo frontend seguindo
 * o padrão dos demais (documentado no HANDOFF_API.md).
 */

/** Decisão de roteamento/orquestração solicitada ao usuário. */
export const decisionRequestedPayloadSchema = z.object({
  decisionId: ulidSchema,
  projectId: ulidSchema,
  title: z.string(),
  /** Contexto da decisão (ex.: política, cota, capacidade), quando houver. */
  reason: z.string().nullable(),
  requestedByAgentId: ulidSchema.nullable(),
});
/** Decisão resolvida (por humano ou automaticamente). */
export const decisionResolvedPayloadSchema = z.object({
  decisionId: ulidSchema,
  outcome: z.enum(['approved', 'rejected', 'cancelled']),
  resolvedByProfileId: ulidSchema.nullable(),
  note: z.string().nullable(),
});
export const projectCreatedPayloadSchema = z.object({ project: projectSchema });
export const prototypeCreatedPayloadSchema = z.object({ prototype: prototypeSchema });

/* ---- mapa tipo → schema de payload ---- */

export const EVENT_PAYLOAD_SCHEMAS = {
  'chat.turnStarted': chatTurnStartedPayloadSchema,
  'chat.turnChunk': chatTurnChunkPayloadSchema,
  'chat.turnCompleted': chatTurnCompletedPayloadSchema,
  'message.appended': messageAppendedPayloadSchema,
  'demand.created': demandCreatedPayloadSchema,
  'task.created': taskCreatedPayloadSchema,
  'task.stateChanged': taskStateChangedPayloadSchema,
  'attempt.started': attemptStartedPayloadSchema,
  'attempt.heartbeat': attemptHeartbeatPayloadSchema,
  'attempt.completed': attemptCompletedPayloadSchema,
  'attempt.failed': attemptFailedPayloadSchema,
  'gate.changed': gateChangedPayloadSchema,
  'approval.requested': approvalRequestedPayloadSchema,
  'approval.resolved': approvalResolvedPayloadSchema,
  'document.stateChanged': documentStateChangedPayloadSchema,
  'prototype.stateChanged': prototypeStateChangedPayloadSchema,
  'workflow.versionPublished': workflowVersionPublishedPayloadSchema,
  'notification.created': notificationCreatedPayloadSchema,
  'agent.statusChanged': agentStatusChangedPayloadSchema,
  'tool.statusChanged': toolStatusChangedPayloadSchema,
  'audit.eventAppended': auditEventAppendedPayloadSchema,
  'run.logAppended': runLogAppendedPayloadSchema,
  'progress.updated': progressUpdatedPayloadSchema,
  'quota.updated': quotaUpdatedPayloadSchema,
  'chief.turnStateChanged': chiefTurnStateChangedPayloadSchema,
  'decision.requested': decisionRequestedPayloadSchema,
  'decision.resolved': decisionResolvedPayloadSchema,
  'project.created': projectCreatedPayloadSchema,
  'prototype.created': prototypeCreatedPayloadSchema,
} as const;

export type EventType = keyof typeof EVENT_PAYLOAD_SCHEMAS;
export const EVENT_TYPES = Object.keys(EVENT_PAYLOAD_SCHEMAS) as EventType[];

/** Mapa tipo → payload inferido dos schemas. */
export type EventPayloadMap = {
  [K in EventType]: z.infer<(typeof EVENT_PAYLOAD_SCHEMAS)[K]>;
};

/** Envelope tipado por tipo de evento. */
export type EventEnvelope<T extends EventType = EventType> = {
  [K in EventType]: {
    stream: string;
    sequence: number;
    type: K;
    occurredAt: string;
    payload: EventPayloadMap[K];
  };
}[T];

/** Schema do envelope completo (união discriminada por `type`). */
export const eventEnvelopeSchema = z.discriminatedUnion('type', [
  z.object({ ...envelopeBase, type: z.literal('chat.turnStarted'), payload: chatTurnStartedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('chat.turnChunk'), payload: chatTurnChunkPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('chat.turnCompleted'), payload: chatTurnCompletedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('message.appended'), payload: messageAppendedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('demand.created'), payload: demandCreatedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('task.created'), payload: taskCreatedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('task.stateChanged'), payload: taskStateChangedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('attempt.started'), payload: attemptStartedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('attempt.heartbeat'), payload: attemptHeartbeatPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('attempt.completed'), payload: attemptCompletedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('attempt.failed'), payload: attemptFailedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('gate.changed'), payload: gateChangedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('approval.requested'), payload: approvalRequestedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('approval.resolved'), payload: approvalResolvedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('document.stateChanged'), payload: documentStateChangedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('prototype.stateChanged'), payload: prototypeStateChangedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('workflow.versionPublished'), payload: workflowVersionPublishedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('notification.created'), payload: notificationCreatedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('agent.statusChanged'), payload: agentStatusChangedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('tool.statusChanged'), payload: toolStatusChangedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('audit.eventAppended'), payload: auditEventAppendedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('run.logAppended'), payload: runLogAppendedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('progress.updated'), payload: progressUpdatedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('quota.updated'), payload: quotaUpdatedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('chief.turnStateChanged'), payload: chiefTurnStateChangedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('decision.requested'), payload: decisionRequestedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('decision.resolved'), payload: decisionResolvedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('project.created'), payload: projectCreatedPayloadSchema }),
  z.object({ ...envelopeBase, type: z.literal('prototype.created'), payload: prototypeCreatedPayloadSchema }),
]);

/** Snapshot canônico retornado pelo hub e pelo fallback HTTP de re-sync. */
export const eventStreamSnapshotSchema = z.object({
  stream: z.string(),
  sequence: z.coerce.number().int().nonnegative(),
  latestByType: z.record(eventEnvelopeSchema),
  delta: z.array(eventEnvelopeSchema),
});
export type EventStreamSnapshot = z.infer<typeof eventStreamSnapshotSchema>;

/** Valida um envelope desconhecido vindo do hub. */
export function parseEventEnvelope(raw: unknown): EventEnvelope {
  return eventEnvelopeSchema.parse(raw) as EventEnvelope;
}
