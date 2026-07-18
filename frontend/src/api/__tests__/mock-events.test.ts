import { describe, expect, it } from 'vitest';

import { streams, type EventEnvelope } from '../contracts';
import { createTestBundle } from './test-utils';

describe('MockApiClient + MockRealtimeClient: mutações emitem eventos', () => {
  it('criar tarefa emite task.created com sequence correta no stream do projeto', async () => {
    const { api, realtime, fixtures } = createTestBundle();
    const project = fixtures.data.projects[0];
    const received: EventEnvelope[] = [];
    realtime.subscribe(streams.project(project.id), (event) => received.push(event));

    const task = await api.create('tasks', {
      projectId: project.id,
      title: 'Tarefa criada no teste de evento',
      instruction: 'Instrução inicial imutável.',
    });

    expect(received).toHaveLength(1);
    const event = received[0];
    expect(event.type).toBe('task.created');
    expect(event.sequence).toBe(1);
    expect(event.stream).toBe(streams.project(project.id));
    if (event.type === 'task.created') {
      expect(event.payload.task.id).toBe(task.id);
      expect(event.payload.task.state).toBe('backlog');
    }
  });

  it('mover tarefa emite task.stateChanged com from/to e sequence seguinte', async () => {
    const { api, realtime, fixtures } = createTestBundle();
    const project = fixtures.data.projects[0];
    const task = fixtures.data.tasks.find(
      (t) => t.projectId === project.id && t.state === 'backlog',
    )!;
    const received: EventEnvelope[] = [];
    realtime.subscribe(streams.project(project.id), (event) => received.push(event));

    await api.moveTask(task.id, { toState: 'ready' });

    expect(received).toHaveLength(1);
    const event = received[0];
    expect(event.sequence).toBe(1);
    if (event.type === 'task.stateChanged') {
      expect(event.payload.from).toBe('backlog');
      expect(event.payload.to).toBe('ready');
      expect(event.payload.taskId).toBe(task.id);
    } else {
      throw new Error(`evento inesperado: ${event.type}`);
    }

    // Segunda mutação no mesmo stream incrementa a sequence.
    await api.moveTask(task.id, { toState: 'development' });
    expect(received[1].sequence).toBe(2);
  });

  it('resolver aprovação de gate emite approval.resolved e gate.changed', async () => {
    const { api, realtime, fixtures } = createTestBundle();
    const approval = fixtures.data.approvals.find((a) => a.state === 'pending' && a.gateId)!;
    const received: EventEnvelope[] = [];
    realtime.subscribe(streams.project(approval.projectId), (event) => received.push(event));

    await api.resolveApproval(approval.id, { decision: 'approved' });

    const types = received.map((e) => e.type);
    expect(types).toContain('approval.resolved');
    expect(types).toContain('gate.changed');
    // Sequences crescem na ordem de emissão no stream.
    expect(received.map((e) => e.sequence)).toEqual([1, 2]);
  });

  it('startChatTurn emite message.appended + fluxo de turno por chunks', async () => {
    const { api, realtime, fixtures } = createTestBundle({ chatChunkDelayMs: 5 });
    const conversation = fixtures.data.conversations[0];
    const received: EventEnvelope[] = [];
    realtime.subscribe(streams.conversation(conversation.id), (event) => received.push(event));

    const handle = await api.startChatTurn(conversation.id, { content: 'Olá, chefe!' });
    expect(handle.conversationId).toBe(conversation.id);

    // Aguarda o fluxo completo (chunks + completion, delays de 5ms).
    await new Promise((resolve) => setTimeout(resolve, 200));

    const types = received.map((e) => e.type);
    expect(types[0]).toBe('message.appended'); // mensagem do usuário
    expect(types).toContain('chat.turnStarted');
    expect(types).toContain('chat.turnChunk');
    expect(types).toContain('chat.turnCompleted');
    // Resposta do chefe persistida ao final do turno.
    const messages = await api.list('messages', { filter: { conversationId: conversation.id } });
    expect(messages.items.some((m) => m.authorRole === 'chief' && m.content.length > 0)).toBe(true);
    expect(received.every((e, i) => e.sequence === i + 1)).toBe(true);
  });
});
