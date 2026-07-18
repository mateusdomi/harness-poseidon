import { afterEach, describe, expect, it, vi } from 'vitest';

import { createApi } from '../index';
import { HttpApiClient, MockApiClient } from '../client';
import { MockRealtimeClient, SignalRRealtimeClient } from '../realtime';

describe('factory createApi', () => {
  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it('VITE_API_MODE=mock retorna MockApiClient + MockRealtimeClient', () => {
    vi.stubEnv('VITE_API_MODE', 'mock');
    const bundle = createApi();
    expect(bundle.api).toBeInstanceOf(MockApiClient);
    expect(bundle.realtime).toBeInstanceOf(MockRealtimeClient);
  });

  it('sem VITE_API_MODE, o default é mock', () => {
    vi.stubEnv('VITE_API_MODE', '');
    const bundle = createApi();
    expect(bundle.api).toBeInstanceOf(MockApiClient);
  });

  it('VITE_API_MODE=http retorna HttpApiClient + SignalRRealtimeClient', () => {
    vi.stubEnv('VITE_API_MODE', 'http');
    vi.stubEnv('VITE_API_BASE_URL', 'https://api.poseidon.test');
    const bundle = createApi();
    expect(bundle.api).toBeInstanceOf(HttpApiClient);
    expect(bundle.realtime).toBeInstanceOf(SignalRRealtimeClient);
    // Não conecta sozinho: a conexão é explícita (AppProviders/teste).
    expect(bundle.realtime.state).toBe('disconnected');
  });

  it('parâmetro explícito de modo tem precedência sobre o env', () => {
    vi.stubEnv('VITE_API_MODE', 'http');
    vi.stubEnv('VITE_API_BASE_URL', 'https://api.poseidon.test');
    const bundle = createApi('mock');
    expect(bundle.api).toBeInstanceOf(MockApiClient);
  });
});
