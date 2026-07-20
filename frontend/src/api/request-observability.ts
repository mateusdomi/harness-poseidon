export const API_REQUEST_EVENT = 'poseidon:api-request';
export const API_SLOW_REQUEST_MS = 4_000;
export const API_READ_TIMEOUT_MS = 10_000;

export type ApiRequestPhase = 'started' | 'slow' | 'settled';

export interface ApiRequestTelemetry {
  requestId: string;
  method: string;
  path: string;
  phase: ApiRequestPhase;
  durationMs: number;
  status?: number;
}

/** Publica telemetria local sem query string, payload, cookie ou segredo. */
export function publishApiRequestTelemetry(detail: ApiRequestTelemetry) {
  if (typeof window === 'undefined') return;
  window.dispatchEvent(new CustomEvent<ApiRequestTelemetry>(API_REQUEST_EVENT, { detail }));
  if (detail.phase === 'settled' && typeof performance?.measure === 'function') {
    try {
      performance.measure('poseidon.api.request', {
        start: performance.now() - detail.durationMs,
        end: performance.now(),
        detail,
      });
    } catch {
      // Browsers sem suporte ao overload com detail ainda recebem o CustomEvent tipado.
    }
  }
}

export function safeRequestPath(url: string) {
  try {
    return new URL(url, typeof location === 'undefined' ? 'http://localhost' : location.origin)
      .pathname;
  } catch {
    return '/api/v1/unknown';
  }
}
