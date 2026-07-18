import { MockApiClient, type MockApiClientOptions } from '../client';
import { MockRealtimeClient } from '../realtime';
import { DeterministicUlidGenerator, buildFixtures, mulberry32, type FixtureData } from '../fixtures';

export interface TestBundle {
  api: MockApiClient;
  realtime: MockRealtimeClient;
  fixtures: FixtureData;
}

/**
 * Bundle mock determinístico para testes: latência mínima, PRNG com seed,
 * relógio fixo e realtime em memória (sem heartbeats automáticos).
 */
export function createTestBundle(overrides: Partial<MockApiClientOptions> = {}): TestBundle {
  const fixtures = buildFixtures(42);
  const realtime = new MockRealtimeClient({ connectDelayMs: 1 });
  const ids = new DeterministicUlidGenerator(777);
  const api = new MockApiClient(fixtures, {
    latency: { min: 1, max: 3 },
    random: mulberry32(99),
    nextId: () => ids.next(),
    now: () => '2026-07-17T12:00:00Z',
    currentProfileId: fixtures.meta.currentProfileId,
    realtime,
    ...overrides,
  });
  return { api, realtime, fixtures };
}
