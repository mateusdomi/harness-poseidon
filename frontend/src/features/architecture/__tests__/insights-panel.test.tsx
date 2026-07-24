import { screen } from '@testing-library/react';

import { renderWithApi } from '@/test/render-with-providers';
import { ArchitectureApiProvider } from '../api/architecture-provider';
import { MockArchitectureApi } from '../api/architecture-api';
import { InsightsPanel } from '../components/hub/insights-panel';
import { DiscoveryPanel } from '../components/hub/discovery-panel';

describe('InsightsPanel (ARC-07)', () => {
  it('mostra o relatório de racionalização com classificação', async () => {
    const api = new MockArchitectureApi();
    renderWithApi(
      <ArchitectureApiProvider client={api}>
        <InsightsPanel projectId="PROJ1" />
      </ArchitectureApiProvider>,
    );

    expect(await screen.findByTestId('insights-panel')).toBeInTheDocument();
    expect(screen.getByText('Faturamento Legado')).toBeInTheDocument();
    // Classificação TIME "Eliminar" presente para a capacidade duplicada.
    expect(screen.getAllByText('Eliminar').length).toBeGreaterThan(0);
  });
});

describe('DiscoveryPanel (ARC-06)', () => {
  it('mostra descobertas e o resumo por assunto', async () => {
    const api = new MockArchitectureApi();
    renderWithApi(
      <ArchitectureApiProvider client={api}>
        <DiscoveryPanel projectId="PROJ1" />
      </ArchitectureApiProvider>,
    );

    expect(await screen.findByTestId('discovery-summary')).toBeInTheDocument();
    expect(screen.getAllByText('Faturamento Legado').length).toBeGreaterThan(0);
    expect(screen.getByText('Perguntas pendentes')).toBeInTheDocument();
  });
});
