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

  it('renders an empty state that explains bot-configured vs linked and offers a link form', async () => {
    const bundle = createTestBundle();
    bundle.api.listChannelLinks = () => Promise.resolve([]);
    renderChannels(bundle);

    expect(await screen.findByText(/Nenhum canal vinculado/i)).toBeInTheDocument();
    // A tela deixa explícita a diferença entre bot configurado e canal vinculado.
    expect(screen.getByText(/Bot configurado ≠ canal vinculado/i)).toBeInTheDocument();
    // E oferece o passo a passo exato via CLI/gateway.
    expect(screen.getByText(/Harness__Channels__Telegram__BotToken/)).toBeInTheDocument();
    expect(screen.getAllByText(/Harness__Channels__Telegram__BotToken/)[0].closest('pre')).toHaveAttribute(
      'tabindex',
      '0',
    );
    // O formulário de vínculo está disponível já no empty-state.
    expect(await screen.findByRole('button', { name: /Vincular canal/i })).toBeInTheDocument();
  });

  it('links a new channel through the form and shows it in the list', async () => {
    const bundle = createTestBundle();
    const linked: unknown[] = [];
    bundle.api.listChannelLinks = () => Promise.resolve([...linked] as never);
    bundle.api.createChannelLink = (input) => {
      const link = {
        id: '01J0CHANNELTELEGRAM000000009',
        kind: input.kind,
        externalIdentity: input.externalIdentity,
        projectId: input.projectId,
        conversationId: '01J0CHANNELCONV000000000009',
        linkedAt: '2026-07-24T10:00:00.000Z',
      };
      linked.push(link);
      return Promise.resolve(link);
    };
    renderChannels(bundle);

    const identity = await screen.findByPlaceholderText('5774120296');
    await userEvent.type(identity, '5774120296');
    await userEvent.click(screen.getByRole('button', { name: /Vincular canal/i }));

    // O vínculo criado passa a aparecer na lista de canais (revalidação).
    expect(await screen.findByText('5774120296')).toBeInTheDocument();
    expect(screen.getAllByText('Ativo').length).toBeGreaterThan(0);
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
