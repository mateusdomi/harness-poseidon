import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

import {
  EVENT_TYPES,
  eventEnvelopeSchema,
  parseEventEnvelope,
  RESOURCE_KINDS,
  RESOURCE_SCHEMAS,
  streams,
  type EventEnvelope,
  type EventType,
} from '../contracts';
import { fixtures } from '../fixtures';

/** Envelope de exemplo válido para cada tipo de evento do catálogo. */
function sampleEnvelopes(): Record<EventType, EventEnvelope> {
  const d = fixtures.data;
  const task = d.tasks[0];
  const attempt = d.attempts[0];
  const approval = d.approvals[0];
  const gate = d.gates[0];
  const conversation = d.conversations[0];
  const project = d.projects[0];
  const occurredAt = '2026-07-17T12:00:00Z';
  const base = { stream: streams.project(project.id), sequence: 1, occurredAt };

  return {
    'chat.turnStarted': { ...base, stream: streams.conversation(conversation.id), type: 'chat.turnStarted', payload: { conversationId: conversation.id, turnId: task.id, agentId: project.chiefAgentId } },
    'chat.turnChunk': { ...base, stream: streams.conversation(conversation.id), type: 'chat.turnChunk', payload: { conversationId: conversation.id, turnId: task.id, index: 0, text: 'Olá! ' } },
    'chat.turnCompleted': { ...base, stream: streams.conversation(conversation.id), type: 'chat.turnCompleted', payload: { conversationId: conversation.id, turnId: task.id, messageId: d.messages[0].id, finishReason: 'stop' } },
    'message.appended': { ...base, stream: streams.conversation(conversation.id), type: 'message.appended', payload: { message: d.messages[0] } },
    'demand.created': { ...base, type: 'demand.created', payload: { demand: d.demands[0] } },
    'task.created': { ...base, type: 'task.created', payload: { task } },
    'task.stateChanged': { ...base, type: 'task.stateChanged', payload: { taskId: task.id, from: 'ready', to: 'development', changedByKind: 'chief', note: null } },
    'attempt.started': { ...base, type: 'attempt.started', payload: { attempt } },
    'attempt.heartbeat': { ...base, type: 'attempt.heartbeat', payload: { attemptId: attempt.id, taskId: attempt.taskId, elapsedMs: 5_000, tokensInput: 480, tokensOutput: 160, costUsd: 0.004 } },
    'attempt.completed': { ...base, type: 'attempt.completed', payload: { attemptId: attempt.id, taskId: attempt.taskId, durationMs: 120_000, tokensInput: 12_000, tokensOutput: 4_000, costUsd: 0.42, commitRefs: ['feat:csv'], summary: 'Concluído.' } },
    'attempt.failed': { ...base, type: 'attempt.failed', payload: { attemptId: attempt.id, taskId: attempt.taskId, reason: 'Testes falharam.', durationMs: 60_000, costUsd: 0.1 } },
    'gate.changed': { ...base, type: 'gate.changed', payload: { gateId: gate.id, runId: gate.runId, from: 'pending', to: 'approved', decidedByProfileId: d.profiles[0].id, note: null } },
    'approval.requested': { ...base, type: 'approval.requested', payload: { approval } },
    'approval.resolved': { ...base, type: 'approval.resolved', payload: { approvalId: approval.id, state: 'approved', resolvedByProfileId: d.profiles[0].id, note: 'OK.' } },
    'document.stateChanged': { ...base, type: 'document.stateChanged', payload: { documentId: d.documents[0].id, from: 'inReview', to: 'awaitingApproval' } },
    'prototype.stateChanged': { ...base, type: 'prototype.stateChanged', payload: { prototypeId: d.prototypes[0].id, from: 'draft', to: 'ready' } },
    'workflow.versionPublished': { ...base, type: 'workflow.versionPublished', payload: { templateId: d['workflow-templates'][0].id, versionId: d['workflow-versions'][0].id, version: 1 } },
    'notification.created': { ...base, stream: streams.profile(d.profiles[0].id), type: 'notification.created', payload: { notification: d.notifications[0] } },
    'agent.statusChanged': { ...base, stream: streams.global(), type: 'agent.statusChanged', payload: { agentId: d.agents[0].id, from: 'idle', to: 'working', currentTaskId: task.id } },
    'tool.statusChanged': { ...base, stream: streams.global(), type: 'tool.statusChanged', payload: { toolId: d.tools[0].id, from: 'enabled', to: 'error' } },
    'audit.eventAppended': { ...base, stream: streams.global(), type: 'audit.eventAppended', payload: { auditEvent: d['audit-events'][0] } },
    'run.logAppended': { ...base, stream: streams.run(d['workflow-runs'][0].id), type: 'run.logAppended', payload: { runId: d['workflow-runs'][0].id, attemptId: null, line: '[12:00:00] fase Validação iniciada' } },
    'progress.updated': { ...base, type: 'progress.updated', payload: { taskId: task.id, track: 'executed', value: 55, progress: { executed: 55, validated: 0, approved: 0 } } },
    'quota.updated': { ...base, type: 'quota.updated', payload: { accountId: d.accounts[0].id, budgetId: null, usedUsd: 88.1, limitUsd: 150 } },
    'chief.turnStateChanged': { ...base, stream: streams.conversation(conversation.id), type: 'chief.turnStateChanged', payload: { conversationId: conversation.id, turnId: task.id, state: 'delegating' } },
    'decision.requested': { ...base, type: 'decision.requested', payload: { decisionId: approval.id, projectId: project.id, title: 'Trocar modelo do revisor?', reason: 'quota', requestedByAgentId: project.chiefAgentId } },
    'decision.resolved': { ...base, type: 'decision.resolved', payload: { decisionId: approval.id, outcome: 'approved', resolvedByProfileId: d.profiles[0].id, note: null } },
    'project.created': { ...base, stream: streams.global(), type: 'project.created', payload: { project } },
    'prototype.created': { ...base, type: 'prototype.created', payload: { prototype: d.prototypes[0] } },
    // Catálogo 1.1 com payload ainda não publicado pelo backend: validamos o
    // envelope, não campos que ninguém especificou.
    'readiness.changed': { ...base, type: 'readiness.changed', payload: { projectId: project.id } },
    'execution.blocked': { ...base, type: 'execution.blocked', payload: { projectId: project.id } },
    'execution.enqueued': { ...base, type: 'execution.enqueued', payload: { projectId: project.id } },
    'message.received': { ...base, type: 'message.received', payload: { conversationId: conversation.id } },
    'model.responded': { ...base, type: 'model.responded', payload: { modelId: d.models[0].id } },
    'provider.invoked': { ...base, type: 'provider.invoked', payload: { providerId: d.providers[0].id } },
    'turn.registered': { ...base, type: 'turn.registered', payload: { conversationId: conversation.id } },
  };
}

describe('contracts: fixtures × schemas Zod', () => {
  it.each(RESOURCE_KINDS)('todas as fixtures de "%s" validam no schema', (resource) => {
    const schema = RESOURCE_SCHEMAS[resource];
    const items = fixtures.data[resource];
    expect(items.length).toBeGreaterThan(0);
    for (const item of items) {
      const result = schema.safeParse(item);
      expect(result.success, JSON.stringify(result.success ? null : result.error.issues)).toBe(true);
    }
  });

  it('fixtures têm 30+ tarefas distribuídas nas colunas do quadro', () => {
    const states = new Set(fixtures.data.tasks.map((t) => t.state));
    expect(fixtures.data.tasks.length).toBeGreaterThanOrEqual(30);
    expect(states.size).toBe(8);
  });
});

describe('contracts: envelope de evento', () => {
  it('catálogo do frontend cobre exatamente o catálogo canônico', () => {
    // Sem número mágico: a fonte é `docs/contracts/events.json`. O teste de
    // drift cobre a paridade nos dois sentidos; aqui garantimos a contagem.
    const catalogPath = resolve(process.cwd(), '..', 'docs', 'contracts', 'events.json');
    const catalog = JSON.parse(readFileSync(catalogPath, 'utf-8')) as { events: string[] };
    expect(EVENT_TYPES).toHaveLength(catalog.events.length);
  });

  it.each(EVENT_TYPES)('envelope "%s" faz round-trip JSON → parse', (type) => {
    const envelope = sampleEnvelopes()[type];
    const wire = JSON.parse(JSON.stringify(envelope)) as unknown;
    const parsed = eventEnvelopeSchema.parse(wire);
    expect(parsed.type).toBe(type);
    expect(parseEventEnvelope(wire).sequence).toBe(1);
  });

  it('rejeita envelope com payload fora do contrato', () => {
    expect(() =>
      parseEventEnvelope({
        stream: 'project:01J',
        sequence: 1,
        type: 'task.stateChanged',
        occurredAt: '2026-07-17T12:00:00Z',
        payload: { taskId: 'não-é-ulid', from: 'ready', to: 'development' },
      }),
    ).toThrow();
  });
});
