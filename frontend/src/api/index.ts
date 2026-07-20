import { HttpApiClient, MockApiClient, type ApiClient } from './client';
import {
  MockRealtimeClient,
  SignalRRealtimeClient,
  type RealtimeClient,
} from './realtime';
import { buildFixtures, DeterministicUlidGenerator, FIXTURE_SEED } from './fixtures';

export * from './contracts';
export * from './client';
export * from './realtime';
export * from './request-observability';
export {
  buildFixtures,
  createTickClock,
  DeterministicUlidGenerator,
  FIXTURE_SEED,
  fixtures,
  mulberry32,
} from './fixtures';
export type { FixtureData } from './fixtures';

/** Par API + realtime que a aplicação consome via contexto. */
export interface ApiBundle {
  api: ApiClient;
  realtime: RealtimeClient;
}

/**
 * Factory da camada de dados.
 * - `VITE_API_MODE=mock` (default): MockApiClient + MockRealtimeClient com
 *   fixtures determinísticas, latência simulada e eventos nas mutações.
 * - `VITE_API_MODE=http` + `VITE_API_BASE_URL`: HttpApiClient (`/api/v1`)
 *   + SignalRRealtimeClient (`/hubs/events`).
 * Trocar o modo NÃO exige mexer em nenhum componente.
 */
export function createApi(mode: string = import.meta.env.VITE_API_MODE ?? 'mock'): ApiBundle {
  if (mode === 'http') {
    const baseUrl = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5001';
    return {
      api: new HttpApiClient({ baseUrl }),
      realtime: new SignalRRealtimeClient({ baseUrl }),
    };
  }

  const fixtureData = buildFixtures(FIXTURE_SEED);
  const realtime = new MockRealtimeClient();
  const ids = new DeterministicUlidGenerator(FIXTURE_SEED + 1_000);
  const api = new MockApiClient(fixtureData, {
    nextId: () => ids.next(),
    currentProfileId: fixtureData.meta.currentProfileId,
    realtime,
    simulateHeartbeats: true,
  });
  return { api, realtime };
}
