import { describe, expect, it } from 'vitest';

import type { EventEnvelope } from '../contracts';
import { MockRealtimeClient, SequenceTracker } from '../realtime';

const STREAM = 'project:01J9QH5Z3W8K2M4P6R8T0V2X4Y';
const TASK_ID = '01J9QH5Z3W8K2M4P6R8T0V2X4Y';

function statePayload(
  from: 'ready' | 'development' | 'review',
  to: 'ready' | 'development' | 'review',
) {
  return { taskId: TASK_ID, from, to, changedByKind: 'user' as const, note: null };
}

describe('MockRealtimeClient', () => {
  it('entrega eventos a múltiplos subscribers com sequence crescente por stream', () => {
    const realtime = new MockRealtimeClient();
    const a: EventEnvelope[] = [];
    const b: EventEnvelope[] = [];
    realtime.subscribe(STREAM, (e) => a.push(e));
    realtime.subscribe(STREAM, (e) => b.push(e));

    realtime.emit(STREAM, 'task.stateChanged', statePayload('ready', 'development'));
    realtime.emit(STREAM, 'task.stateChanged', statePayload('development', 'review'));
    realtime.emit('project:OUTRO', 'task.stateChanged', statePayload('ready', 'development'));

    expect(a.map((e) => e.sequence)).toEqual([1, 2]);
    expect(b).toHaveLength(2);
  });

  it('unsubscribe para de receber eventos', () => {
    const realtime = new MockRealtimeClient();
    const received: EventEnvelope[] = [];
    const subscription = realtime.subscribe(STREAM, (e) => received.push(e));

    realtime.emit(STREAM, 'task.stateChanged', statePayload('ready', 'development'));
    subscription.unsubscribe();
    realtime.emit(STREAM, 'task.stateChanged', statePayload('development', 'review'));

    expect(received).toHaveLength(1);
  });

  it('dedupe: SequenceTracker descarta evento repetido (replay)', () => {
    const realtime = new MockRealtimeClient();
    const tracker = new SequenceTracker();
    const applied: EventEnvelope[] = [];
    realtime.subscribe(STREAM, (event) => {
      if (tracker.check(event) === 'applied') applied.push(event);
    });

    realtime.emit(STREAM, 'task.stateChanged', statePayload('ready', 'development'));
    const segundo = realtime.emit(STREAM, 'task.stateChanged', statePayload('development', 'review'));
    // Servidor reenvia o segundo evento (retry/replay na reconexão).
    realtime.replay(segundo);
    realtime.replay(segundo);

    expect(applied).toHaveLength(2);
    expect(tracker.lastSequence(STREAM)).toBe(2);
  });

  it('lacuna de sequence é detectada e resolvida com snapshot (re-sync)', async () => {
    const realtime = new MockRealtimeClient();
    const tracker = new SequenceTracker();
    const applied: EventEnvelope[] = [];
    const gaps: string[] = [];

    realtime.subscribe(STREAM, (event) => {
      const check = tracker.check(event);
      if (check === 'applied') applied.push(event);
      if (check === 'gap') gaps.push(event.stream);
    });

    realtime.emit(STREAM, 'task.stateChanged', statePayload('ready', 'development')); // seq 1
    realtime.emit(STREAM, 'task.stateChanged', statePayload('development', 'review')); // seq 2
    // Perda simulada: o seq 3 nunca chega; o próximo entregue é o seq 4.
    realtime.emitOutOfOrder(STREAM, 4, 'task.stateChanged', statePayload('review', 'development'));

    expect(gaps).toEqual([STREAM]);
    expect(tracker.lastSequence(STREAM)).toBe(2);
    // Evento com lacuna NÃO é aplicado: o consumidor pede snapshot (re-sync).
    expect(applied.map((e) => e.sequence)).toEqual([1, 2]);

    // Re-sync: snapshot do stream traz os eventos 1–2 (do log) — todos duplicados.
    const snapshot = await realtime.getSnapshot(STREAM);
    expect(snapshot.map((e) => e.sequence)).toEqual([1, 2]);
    for (const event of snapshot) {
      expect(tracker.check(event)).toBe('duplicate');
    }

    // Depois do re-sync, a sequência ausente e o envelope pendente aplicam em ordem.
    realtime.emit(STREAM, 'task.stateChanged', statePayload('development', 'review')); // seq 3 do log
    realtime.emitOutOfOrder(STREAM, 4, 'task.stateChanged', statePayload('review', 'development'));
    expect(applied.map((e) => e.sequence)).toEqual([1, 2, 3, 4]);
    expect(tracker.lastSequence(STREAM)).toBe(4);
  });

  it('expõe estado de conexão (connected/reconnecting/disconnected)', async () => {
    const realtime = new MockRealtimeClient({ connectDelayMs: 5 });
    const states: string[] = [];
    realtime.onStateChange((state) => states.push(state));

    expect(realtime.state).toBe('disconnected');
    const connecting = realtime.connect();
    expect(realtime.state).toBe('reconnecting');
    await connecting;
    expect(realtime.state).toBe('connected');
    await realtime.disconnect();
    expect(realtime.state).toBe('disconnected');
    expect(states).toEqual(['reconnecting', 'connected', 'disconnected']);
  });

  it('simula heartbeat de tentativa em andamento', async () => {
    const realtime = new MockRealtimeClient();
    const received: EventEnvelope[] = [];
    realtime.subscribe(`attempt:${TASK_ID}`, (e) => received.push(e));

    realtime.startAttemptHeartbeat({ attemptId: TASK_ID, taskId: TASK_ID, intervalMs: 10 });
    await new Promise((resolve) => setTimeout(resolve, 55));
    realtime.dispose();

    expect(received.length).toBeGreaterThanOrEqual(3);
    expect(received.every((e) => e.type === 'attempt.heartbeat')).toBe(true);
    expect(received.map((e) => e.sequence)).toEqual(
      received.map((_, i) => i + 1),
    );
  });
});
