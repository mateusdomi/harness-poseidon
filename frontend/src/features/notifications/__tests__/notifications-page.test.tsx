import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { createTestBundle } from '@/api/__tests__/test-utils';
import NotificationsPage from '@/features/notifications/pages/notifications-page';
import { renderWithApi } from '@/test/render-with-providers';

function renderNotifications() {
  // Bundle novo por teste: o store do mock é mutável (notificações/settings).
  const bundle = createTestBundle();
  return renderWithApi(
    <MemoryRouter initialEntries={['/notifications']}>
      <Routes>
        <Route path="/notifications" element={<NotificationsPage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

describe('UnotificationsPage', () => {
  it('lista a central ordenada por mais recente, com grupo deduplicado', async () => {
    renderNotifications();

    const center = await screen.findByRole('list', { name: 'Central de notificações' });
    const items = within(center).getAllByRole('listitem');
    // 10 notificações; os 2 groupKeys têm 1 item cada → 10 entradas.
    expect(items).toHaveLength(10);
    // Fixture é criada em sequência: a última (budget) é a mais recente.
    expect(within(items[0]).getByText('Budget do projeto em 42%')).toBeInTheDocument();
    // Card de grupo: representante + badge com dedupeCount total.
    expect(within(items[0]).getByText('2 ocorrências')).toBeInTheDocument();
    const quotaItem = items.find(
      (li) => within(li).queryByText('Cota em 80%') !== null,
    )!;
    expect(within(quotaItem).getByText('3 ocorrências')).toBeInTheDocument();
    // Deep-link do item: abre o contexto (ex.: /approvals).
    expect(
      within(items[items.length - 1]).getByRole('link', { name: 'Abrir contexto' }),
    ).toHaveAttribute('href', '/approvals');
  });

  it('filtra por status e por categoria', async () => {
    const user = userEvent.setup();
    renderNotifications();

    await screen.findByRole('list', { name: 'Central de notificações' });

    await user.selectOptions(screen.getByLabelText('Status'), 'unread');
    let center = screen.getByRole('list', { name: 'Central de notificações' });
    expect(within(center).getAllByRole('listitem')).toHaveLength(4);
    expect(within(center).getByText('Aprovação pendente')).toBeInTheDocument();
    expect(within(center).queryByText('Licença ativa')).not.toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText('Status'), 'all');
    await user.selectOptions(screen.getByLabelText('Categoria'), 'workflow');
    center = screen.getByRole('list', { name: 'Central de notificações' });
    expect(within(center).getAllByRole('listitem')).toHaveLength(2);
    expect(within(center).getByText('Versão publicada')).toBeInTheDocument();
  });

  it('marca como lida por item e em lote; invalida o badge do shell', async () => {
    const user = userEvent.setup();
    const { queryClient } = renderNotifications();
    // Query do badge do shell (['notifications','unread']) no mesmo client.
    queryClient.setQueryData(['notifications', 'unread'], { items: [], nextCursor: null });

    const center = await screen.findByRole('list', { name: 'Central de notificações' });
    const item = within(center)
      .getAllByRole('listitem')
      .find((li) => within(li).queryByText('Aprovação pendente') !== null)!;

    await user.click(within(item).getByRole('button', { name: 'Marcar como lida' }));
    expect(await within(item).findByText('Lida')).toBeInTheDocument();
    expect(within(item).queryByRole('button', { name: 'Marcar como lida' })).toBeNull();
    // Badge do shell reflete: mesma invalidação por prefixo.
    expect(queryClient.getQueryState(['notifications', 'unread'])?.isInvalidated).toBe(true);

    // Em lote: marca as 3 não lidas restantes.
    await user.click(screen.getByRole('button', { name: 'Marcar todas como lidas' }));
    await waitFor(() => {
      expect(
        screen.queryByRole('button', { name: 'Marcar todas como lidas' }),
      ).not.toBeInTheDocument();
    });
  });

  it('silencia um item e mostra o status silenciado', async () => {
    const user = userEvent.setup();
    renderNotifications();

    const center = await screen.findByRole('list', { name: 'Central de notificações' });
    const item = within(center)
      .getAllByRole('listitem')
      .find((li) => within(li).queryByText('Aprovação pendente') !== null)!;

    await user.click(within(item).getByRole('button', { name: 'Silenciar' }));
    expect(await within(item).findByText('Silenciada')).toBeInTheDocument();
  });

  it('preferências: mutar categoria exibe novas dessa categoria como silenciadas', async () => {
    const user = userEvent.setup();
    renderNotifications();

    const center = await screen.findByRole('list', { name: 'Central de notificações' });
    const item = within(center)
      .getAllByRole('listitem')
      .find((li) => within(li).queryByText('Gate aprovado') !== null)!;
    expect(within(item).getByText('Lida')).toBeInTheDocument();

    await user.click(screen.getByRole('checkbox', { name: 'Workflow' }));
    expect(await within(item).findByText('Silenciada')).toBeInTheDocument();

    // Filtro de silenciadas passa a incluir itens da categoria mutada.
    await user.selectOptions(screen.getByLabelText('Status'), 'muted');
    await waitFor(() => {
      const mutedList = screen.getByRole('list', { name: 'Central de notificações' });
      expect(within(mutedList).getByText('Gate aprovado')).toBeInTheDocument();
    });
  });

  it('notification.created invalida a central e o badge em tempo real', async () => {
    const { bundle, queryClient } = renderNotifications();
    queryClient.setQueryData(['notifications', 'unread'], { items: [], nextCursor: null });

    await screen.findByRole('list', { name: 'Central de notificações' });
    const listSpy = vi.spyOn(bundle.api, 'list');
    listSpy.mockClear();

    const profile = bundle.fixtures.data.profiles.find(
      (candidate) => candidate.id === bundle.fixtures.meta.currentProfileId,
    )!;
    await act(async () => {
      bundle.realtime.emit(`profile:${profile.id}`, 'notification.created', {
        notification: bundle.fixtures.data.notifications[0],
      });
    });

    await waitFor(() => {
      expect(
        listSpy.mock.calls.some(([resource]) => resource === 'notifications'),
      ).toBe(true);
    });
    expect(queryClient.getQueryState(['notifications', 'unread'])?.isInvalidated).toBe(true);
  });
});
