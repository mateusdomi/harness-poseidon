import { render, screen } from '@testing-library/react';

import '@/i18n';

import { DeliveryCharts } from '../components/delivery-charts';
import type { DeliveryFeatureMetric, DeliveryForecast } from '../api/types';

function feature(partial: Partial<DeliveryFeatureMetric> & { featureId: string }): DeliveryFeatureMetric {
  return {
    taskCount: 0,
    attemptCount: 0,
    successCount: 0,
    failureCount: 0,
    totalCostUsd: 0,
    totalTokensInput: 0,
    totalTokensOutput: 0,
    totalDurationMs: 0,
    ...partial,
  };
}

function forecastPoint(percent: number, createdAt: string | null): DeliveryForecast {
  return {
    id: `fc-${percent}`,
    forecastDate: '2026-08-14T00:00:00.000Z',
    confidence: 'medium',
    confidencePercent: percent,
    hasSufficientEvidence: true,
    basis: [],
    createdAt,
  };
}

describe('DeliveryCharts (G-CHARTS)', () => {
  it('grafica marcos, tentativas por feature (sucesso/falha) e tendência de confiança', () => {
    render(
      <DeliveryCharts
        milestonesDone={6}
        milestonesTotal={9}
        features={[
          feature({ featureId: 'FEAT-LOGIN', successCount: 16, failureCount: 2 }),
          feature({ featureId: 'FEAT-DASH', successCount: 31, failureCount: 1 }),
        ]}
        forecastHistory={[
          forecastPoint(61, '2026-07-16T12:00:00.000Z'),
          forecastPoint(82, '2026-07-23T12:00:00.000Z'),
        ]}
      />,
    );

    // Marcos: 6/9 = 67%.
    expect(screen.getByText('67%')).toBeInTheDocument();
    // Feature outcomes: rótulos diretos das contagens de sucesso/falha.
    expect(screen.getByText('FEAT-LOGIN')).toBeInTheDocument();
    expect(screen.getByText('16')).toBeInTheDocument();
    // Tendência: rótulo do ponto mais recente (texto direto + <title> do ponto).
    expect(screen.getAllByText('82%').length).toBeGreaterThan(0);
  });

  it('mostra empty-states honestos quando falta dado (sem features, 1 ponto, sem marcos)', () => {
    render(
      <DeliveryCharts
        milestonesDone={0}
        milestonesTotal={0}
        features={[]}
        forecastHistory={[forecastPoint(40, '2026-07-16T12:00:00.000Z')]}
      />,
    );

    // Uma medição não é tendência → empty-state da previsão.
    expect(
      screen.getByText(/uma medição não é tendência/i),
    ).toBeInTheDocument();
    // Sem features → empty-state.
    expect(screen.getByText(/Sem dados de tentativas por feature/i)).toBeInTheDocument();
    // Sem marcos → empty-state.
    expect(screen.getByText(/Nenhum marco definido/i)).toBeInTheDocument();
  });
});
