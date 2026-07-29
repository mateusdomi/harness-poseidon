import { act, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import '@/i18n';

import { API_REQUEST_EVENT, type ApiRequestTelemetry } from '@/api/request-observability';
import { RequestStatusBanner } from '@/app/components/request-status-banner';
import { ROUTE_SKELETON_TIMEOUT_MS, RouteSkeleton } from '@/app/components/route-skeleton';

afterEach(() => vi.useRealTimers());

function emit(detail: ApiRequestTelemetry) {
  window.dispatchEvent(new CustomEvent(API_REQUEST_EVENT, { detail }));
}

describe('estado seguro de requests e rotas', () => {
  it('mostra request lento e o remove ao encerrar', () => {
    render(<RequestStatusBanner />);
    act(() => emit({ requestId: 'one', method: 'GET', path: '/api/v1/projects', phase: 'slow', durationMs: 4_000 }));
    // Modo Negócio (padrão): mensagem sem caminho técnico.
    expect(screen.getByRole('status')).toHaveTextContent(/demorando para responder/i);
    expect(screen.getByRole('status')).not.toHaveTextContent('/api/v1/projects');
    act(() => emit({ requestId: 'one', method: 'GET', path: '/api/v1/projects', phase: 'settled', durationMs: 4_100, status: 200 }));
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('troca o skeleton de rota por erro com retry após o limite', () => {
    vi.useFakeTimers();
    render(<RouteSkeleton />);
    expect(screen.getByRole('status')).toBeInTheDocument();
    act(() => vi.advanceTimersByTime(ROUTE_SKELETON_TIMEOUT_MS));
    expect(screen.getByRole('alert')).toHaveTextContent(/tempo seguro/i);
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });
});
