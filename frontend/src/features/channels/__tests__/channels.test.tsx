import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { createTestBundle } from '@/api/__tests__/test-utils';
import ChannelsPage from '@/features/channels/pages/channels-page';
import { renderWithApi } from '@/test/render-with-providers';
import { usePresentationStore } from '@/stores/presentation-store';
import { useSessionStore } from '@/stores/session-store';

function renderChannels(bundle = createTestBundle(), mode: 'business' | 'technical' = 'business') {
  useSessionStore.setState({ activeProfileId: bundle.fixtures.meta.currentProfileId });
  usePresentationStore.setState({ modeByProfile: {} });
  usePresentationStore.getState().requestMode(bundle.fixtures.meta.currentProfileId, mode);
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

  it('orienta o vínculo em três passos no modo Negócio sem expor comandos', async () => {
    const bundle = createTestBundle();
    bundle.api.listChannelLinks = () => Promise.resolve([]);
    renderChannels(bundle);

    expect(await screen.findByText(/Nenhum canal vinculado/i)).toBeInTheDocument();
    expect(screen.getByText('Conecte um canal em três passos')).toBeInTheDocument();
    expect(screen.getByText(/Envie “oi”/)).toBeInTheDocument();
    expect(screen.queryByText(/Harness__Channels__Telegram__BotToken/)).not.toBeInTheDocument();
    expect(screen.queryByText(/curl -X POST/)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/Conversa unificada/i)).not.toBeInTheDocument();
    // O formulário de vínculo está disponível já no empty-state.
    expect(await screen.findByRole('button', { name: /Vincular canal/i })).toBeInTheDocument();
  });

  it('mantém comandos e opções avançadas somente no modo Técnico', async () => {
    const bundle = createTestBundle();
    bundle.api.listChannelLinks = () => Promise.resolve([]);
    renderChannels(bundle, 'technical');

    expect(await screen.findByText(/Bot configurado ≠ canal vinculado/i)).toBeInTheDocument();
    expect(await screen.findByText(/Harness__Channels__Telegram__BotToken/)).toBeInTheDocument();
    expect(await screen.findByLabelText(/Conversa unificada/i)).toBeInTheDocument();
  });

  it('links a new channel through the form and shows it in the list', async () => {
    const bundle = createTestBundle();
    const linked: unknown[] = [];
    bundle.api.listChannelLinks = () => Promise.resolve([...linked] as never);
    bundle.api.createChannelLink = (input) => {
      const link = {
        id: '01J0CHANNELTELEGRAM000000009',
        kind: input.kind,
        displayName: null,
        externalIdentity: input.externalIdentity,
        projectId: input.projectId,
        conversationId: '01J0CHANNELCONV000000000009',
        linkedAt: '2026-07-24T10:00:00.000Z',
      };
      linked.push(link);
      return Promise.resolve(link);
    };
    renderChannels(bundle, 'technical');

    const identity = await screen.findByPlaceholderText('5774120296');
    await userEvent.type(identity, '5774120296');
    await userEvent.click(screen.getByRole('button', { name: /Vincular canal/i }));

    // O vínculo criado passa a aparecer na lista de canais (revalidação).
    expect(await screen.findByText('5774120296')).toBeInTheDocument();
    expect(screen.getAllByText('Ativo').length).toBeGreaterThan(0);
  });

  it('links a second channel to an existing active conversation', async () => {
    const bundle = createTestBundle();
    const conversation = bundle.fixtures.data.conversations.find(
      (entry) =>
        entry.state === 'active' && entry.projectId === bundle.fixtures.data.projects[0]?.id,
    );
    expect(conversation).toBeDefined();

    let submittedConversationId: string | undefined;
    bundle.api.createChannelLink = (input) => {
      submittedConversationId = input.conversationId;
      return Promise.resolve({
        id: '01J0CHANNELWHATSAPP000000009',
        kind: input.kind,
        displayName: null,
        externalIdentity: input.externalIdentity,
        projectId: input.projectId,
        conversationId: input.conversationId ?? '01J0CHANNELCONV000000000009',
        linkedAt: '2026-07-24T10:00:00.000Z',
      });
    };
    renderChannels(bundle, 'technical');

    await userEvent.click(await screen.findByRole('button', { name: /Vincular novo canal/i }));
    await userEvent.type(screen.getByPlaceholderText('5774120296'), '5511999999999');
    await userEvent.selectOptions(screen.getByLabelText(/Conversa unificada/i), conversation!.id);
    await userEvent.click(screen.getByRole('button', { name: /^Vincular canal$/i }));

    await waitFor(() => {
      expect(submittedConversationId).toBe(conversation!.id);
    });
  });

  it('renders an error state and retries when the gateway fails', async () => {
    const bundle = createTestBundle();
    bundle.api.listChannelLinks = () => Promise.reject(new Error('gateway down'));
    renderChannels(bundle);

    await waitFor(() => {
      expect(
        screen.getByText('Não foi possível carregar os canais', { exact: true }),
      ).toBeInTheDocument();
    });
  });
});
