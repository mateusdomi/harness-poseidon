import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { createTestBundle } from '@/api/__tests__/test-utils';
import ChannelsPage from '@/features/channels/pages/channels-page';
import { renderWithApi } from '@/test/render-with-providers';

function renderChannels(bundle = createTestBundle()) {
  return renderWithApi(
    <MemoryRouter initialEntries={['/channels']}>
      <Routes>
        <Route path="/channels" element={<ChannelsPage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

describe('ChannelsPage', () => {
  it('lists linked external channels (Telegram) with an active status', async () => {
    renderChannels();

    expect(await screen.findByText('@poseidon_ops_bot:512044')).toBeInTheDocument();
    expect(screen.getByText('Telegram')).toBeInTheDocument();
    expect(screen.getAllByText('Ativo').length).toBeGreaterThan(0);
  });

  it('shows the message history when a channel is selected', async () => {
    renderChannels();
    await screen.findByText('@poseidon_ops_bot:512044');

    // Antes de selecionar, um convite para escolher o canal.
    expect(screen.getByText(/Selecione um canal/i)).toBeInTheDocument();

    const buttons = screen.getAllByRole('button', { name: /Histórico de mensagens/i });
    await userEvent.click(buttons[0]);

    expect(await screen.findByText('Qual o status do golden path?')).toBeInTheDocument();
  });

  it('renders an empty state when no channel is linked', async () => {
    const bundle = createTestBundle();
    bundle.api.listChannelLinks = () => Promise.resolve([]);
    renderChannels(bundle);

    expect(await screen.findByText(/Nenhum canal vinculado/i)).toBeInTheDocument();
  });

  it('renders an error state and retries when the gateway fails', async () => {
    const bundle = createTestBundle();
    bundle.api.listChannelLinks = () => Promise.reject(new Error('gateway down'));
    renderChannels(bundle);

    await waitFor(() => {
      expect(screen.getByText(/Não foi possível carregar os canais/i)).toBeInTheDocument();
    });
  });
});
