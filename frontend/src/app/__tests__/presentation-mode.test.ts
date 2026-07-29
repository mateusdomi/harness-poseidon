import { act, renderHook } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  DEFAULT_PRESENTATION_MODE,
  resolvePresentationProjection,
  usePresentationMode,
} from '@/app/presentation';
import { usePresentationModeStore } from '@/stores/presentation-mode-store';
import { useSessionStore } from '@/stores/session-store';

describe('modo de apresentação — projeção pura', () => {
  it('padrão é Negócio e nada técnico aparece', () => {
    const projection = resolvePresentationProjection(undefined);
    expect(projection.mode).toBe('business');
    expect(projection.isBusiness).toBe(true);
    expect(projection.showTechnicalDetails).toBe(false);
    expect(projection.showAdministrativeActions).toBe(false);
  });

  it('valor persistido inválido cai em Negócio (fail-closed)', () => {
    for (const invalid of ['tecnico', 'BUSINESS', 42, null, {}, '']) {
      expect(resolvePresentationProjection(invalid).mode).toBe(
        DEFAULT_PRESENTATION_MODE,
      );
    }
  });

  it('técnico vê detalhe técnico; só administrador vê ação administrativa', () => {
    const technical = resolvePresentationProjection('technical');
    expect(technical.showTechnicalDetails).toBe(true);
    expect(technical.showAdministrativeActions).toBe(false);
    expect(technical.isBusiness).toBe(false);

    const admin = resolvePresentationProjection('admin');
    expect(admin.showTechnicalDetails).toBe(true);
    expect(admin.showAdministrativeActions).toBe(true);
  });

  it('modo fora dos permitidos volta para Negócio', () => {
    expect(resolvePresentationProjection('admin', ['business']).mode).toBe('business');
  });
});

describe('usePresentationMode — fronteira única das telas', () => {
  beforeEach(() => {
    localStorage.clear();
    usePresentationModeStore.setState({ modeByProfile: {} });
    useSessionStore.setState({ activeProfileId: null });
  });

  it('abre em Negócio quando ninguém escolheu nada', () => {
    const { result } = renderHook(() => usePresentationMode());
    expect(result.current.mode).toBe('business');
    expect(result.current.showTechnicalDetails).toBe(false);
  });

  it('troca de modo e persiste por perfil', () => {
    useSessionStore.setState({ activeProfileId: '01J0000000000000000000000A' });
    const { result } = renderHook(() => usePresentationMode());

    act(() => result.current.setMode('technical'));
    expect(result.current.mode).toBe('technical');
    expect(result.current.showTechnicalDetails).toBe(true);

    // Outro perfil na mesma máquina continua em Negócio.
    act(() => useSessionStore.setState({ activeProfileId: '01J0000000000000000000000B' }));
    expect(result.current.mode).toBe('business');
  });

  it('preferência sobrevive ao recarregar a aplicação (persistência real)', async () => {
    useSessionStore.setState({ activeProfileId: '01J0000000000000000000000A' });
    const first = renderHook(() => usePresentationMode());
    act(() => first.result.current.setMode('admin'));
    first.unmount();
    expect(localStorage.getItem('poseidon-presentation-mode')).toContain('admin');

    // Recarregar a aplicação: módulos zerados, localStorage intacto.
    vi.resetModules();
    const reloadedPresentation = await import('@/app/presentation');
    const reloadedSession = await import('@/stores/session-store');
    reloadedSession.useSessionStore.setState({
      activeProfileId: '01J0000000000000000000000A',
    });

    const second = renderHook(() => reloadedPresentation.usePresentationMode());
    expect(second.result.current.mode).toBe('admin');
    expect(second.result.current.showAdministrativeActions).toBe(true);
  });
});
