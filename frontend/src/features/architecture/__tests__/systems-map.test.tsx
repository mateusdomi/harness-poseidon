import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';

import { renderWithApi } from '@/test/render-with-providers';
import { ArchitectureApiProvider } from '../api/architecture-provider';
import { MockArchitectureApi } from '../api/architecture-api';
import { SystemsMap } from '../components/hub/systems-map';
import { System360Panel } from '../components/hub/system-360';

function renderMap(onSelect = vi.fn()) {
  const api = new MockArchitectureApi();
  renderWithApi(
    <ArchitectureApiProvider client={api}>
      <SystemsMap projectId="PROJ1" onSelectSystem={onSelect} />
    </ArchitectureApiProvider>,
  );
  return onSelect;
}

describe('SystemsMap (ARC-02)', () => {
  it('lista o catálogo de sistemas com criticidade', async () => {
    renderMap();
    expect(await screen.findByRole('button', { name: /Poseidon/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Faturamento Legado/ })).toBeInTheDocument();
  });

  it('filtra o catálogo pela busca', async () => {
    const user = userEvent.setup();
    renderMap();
    await screen.findByRole('button', { name: /Poseidon/ });

    await user.type(screen.getByLabelText('Buscar sistema'), 'Financeiro');
    expect(screen.getByRole('button', { name: /Gateway de Pagamento/ })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Identidade e Acesso/ })).not.toBeInTheDocument();
  });

  it('abre o Sistema 360 ao selecionar um sistema', async () => {
    const user = userEvent.setup();
    const onSelect = renderMap();
    await user.click(await screen.findByRole('button', { name: /Poseidon/ }));
    expect(onSelect).toHaveBeenCalledWith(expect.stringContaining('sys-poseidon'));
  });

  it('mostra a lente de heatmap com sinais de risco', async () => {
    const user = userEvent.setup();
    renderMap();
    await screen.findByRole('button', { name: /Poseidon/ });

    await user.click(screen.getByRole('tab', { name: 'Heatmap' }));
    const heatmap = await screen.findByTestId('systems-heatmap');
    expect(heatmap).toBeInTheDocument();
    expect(screen.getByText('Fim de vida')).toBeInTheDocument();
  });
});

describe('System360Panel (ARC-03)', () => {
  it('mostra as facetas do sistema e o botão de voltar', async () => {
    const api = new MockArchitectureApi();
    const onBack = vi.fn();
    renderWithApi(
      <ArchitectureApiProvider client={api}>
        <System360Panel systemId="PROJ1::sys-poseidon" onBack={onBack} />
      </ArchitectureApiProvider>,
    );

    expect(await screen.findByRole('heading', { name: 'Poseidon' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Negócio' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Governança' })).toBeInTheDocument();

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Voltar' }));
    expect(onBack).toHaveBeenCalled();
  });
});
