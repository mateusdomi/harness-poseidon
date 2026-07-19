import { describe, expect, it, vi } from 'vitest';

import type { EventEnvelope } from '../contracts';
import { SignalRRealtimeClient } from '../realtime';

const stream = 'project:01J9QH5Z3W8K2M4P6R8T0V2X4Y';
const event: EventEnvelope<'task.stateChanged'> = {
  stream,
  sequence: 8,
  type: 'task.stateChanged',
  occurredAt: '2026-07-19T20:00:00Z',
  payload: {
    taskId: '01J9QH5Z3W8K2M4P6R8T0V2X4Y',
    from: 'ready',
    to: 'development',
    changedByKind: 'chief',
    note: null,
  },
};

describe('SignalRRealtimeClient — snapshot HTTP', () => {
  it('lê o envelope canônico e devolve somente o delta após a sequence', async () => {
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(
        JSON.stringify({
          stream,
          sequence: 8,
          latestByType: { 'task.stateChanged': event },
          delta: [event],
        }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      ),
    );
    const client = new SignalRRealtimeClient({
      baseUrl: 'https://api.example.test/',
      fetchFn,
    });

    await expect(client.getSnapshot(stream, 7)).resolves.toEqual([event]);
    expect(fetchFn).toHaveBeenCalledWith(
      `https://api.example.test/api/v1/event-streams/snapshot?stream=${encodeURIComponent(stream)}&afterSequence=7`,
      { credentials: 'include' },
    );
  });

  it('retorna delta vazio quando o endpoint não está disponível', async () => {
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 503 }));
    const client = new SignalRRealtimeClient({ baseUrl: 'https://api.example.test', fetchFn });

    await expect(client.getSnapshot(stream)).resolves.toEqual([]);
  });
});
