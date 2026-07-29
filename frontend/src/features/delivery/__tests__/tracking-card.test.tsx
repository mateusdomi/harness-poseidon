import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { renderWithApi } from '@/test/render-with-providers';

import { TrackingCard, scheduleRisk } from '../components/tracking-card';
import type { DeliverySummary } from '../api/types';

function delivery(overrides: Partial<DeliverySummary> = {}): DeliverySummary {
  return {
    deliveryId: '01JQDEL0000000000000000001',
    projectId: '01JQPRJ0000000000000000001',
    name: 'Loja da Ana',
    key: 'LOJA',
    health: 'healthy',
    predictability: 'stable',
    owner: null,
    committedDate: null,
    forecastDate: null,
    forecastConfidence: null,
    milestonesTotal: 4,
    milestonesDone: 1,
    openTaskCount: 3,
    blockedTaskCount: 0,
    lastActivityAt: '2026-07-20T10:00:00.000Z',
    startedAt: '2026-06-01T09:00:00.000Z',
    targetDeadline: '2026-08-15T00:00:00.000Z',
    attentionSignals: [],
    ...overrides,
  };
}

describe('TrackingCard — rastreamento de encomenda', () => {
  it('mostra quando começou, o prazo combinado e o progresso', () => {
    renderWithApi(<TrackingCard delivery={delivery()} onDownloadDocuments={vi.fn()} />);

    expect(screen.getByText('Começou em')).toBeInTheDocument();
    expect(screen.getByText('Prazo combinado')).toBeInTheDocument();
    expect(screen.getByText('1 de 4 entregas concluídas')).toBeInTheDocument();
  });

  it('sem prazo declarado, diz que não há — não inventa data', () => {
    renderWithApi(
      <TrackingCard delivery={delivery({ targetDeadline: null })} onDownloadDocuments={vi.fn()} />,
    );

    expect(screen.getByText('Sem prazo definido')).toBeInTheDocument();
  });

  it('avisa em linguagem simples quando a previsão passa do prazo', () => {
    renderWithApi(
      <TrackingCard
        delivery={delivery({ forecastDate: '2026-09-01T00:00:00.000Z' })}
        onDownloadDocuments={vi.fn()}
      />,
    );

    expect(screen.getByText(/deve passar do prazo combinado/i)).toBeInTheDocument();
  });

  it('sem prazo não existe atraso: nenhuma orientação de risco', () => {
    renderWithApi(
      <TrackingCard
        delivery={delivery({ targetDeadline: null, forecastDate: '2026-09-01T00:00:00.000Z' })}
        onDownloadDocuments={vi.fn()}
      />,
    );

    expect(screen.queryByText(/prazo combinado\./i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Atenção/)).not.toBeInTheDocument();
  });

  it('oferece o download do que já foi aprovado', async () => {
    const user = userEvent.setup();
    const onDownload = vi.fn();
    renderWithApi(<TrackingCard delivery={delivery()} onDownloadDocuments={onDownload} />);

    await user.click(screen.getByRole('button', { name: /baixar documentos/i }));

    expect(onDownload).toHaveBeenCalledTimes(1);
  });
});

describe('scheduleRisk — julga o cronograma só quando há o que comparar', () => {
  it('sem prazo ou sem previsão, não há veredito', () => {
    expect(scheduleRisk({ targetDeadline: null, forecastDate: '2026-09-01T00:00:00.000Z' })).toBeNull();
    expect(scheduleRisk({ targetDeadline: '2026-09-01T00:00:00.000Z', forecastDate: null })).toBeNull();
  });

  it('previsão depois do prazo é atraso', () => {
    expect(
      scheduleRisk({ targetDeadline: '2026-08-15T00:00:00.000Z', forecastDate: '2026-08-20T00:00:00.000Z' }),
    ).toBe('late');
  });

  it('previsão colada no prazo é aperto, não atraso', () => {
    expect(
      scheduleRisk({ targetDeadline: '2026-08-15T00:00:00.000Z', forecastDate: '2026-08-12T00:00:00.000Z' }),
    ).toBe('tight');
  });

  it('folga confortável não vira alarme', () => {
    expect(
      scheduleRisk({ targetDeadline: '2026-08-15T00:00:00.000Z', forecastDate: '2026-07-01T00:00:00.000Z' }),
    ).toBeNull();
  });
});
