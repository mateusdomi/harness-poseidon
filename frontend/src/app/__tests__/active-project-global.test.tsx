import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';

import i18n from '@/i18n';
import { createTestBundle, type TestBundle } from '@/api/__tests__/test-utils';
import { AppShell } from '@/app/app-shell';
import { useActiveProjectStore } from '@/stores/active-project-store';
import { usePresentationStore } from '@/stores/presentation-store';
import { useSessionStore } from '@/stores/session-store';
import { useUiStore } from '@/stores/ui-store';
import { renderWithApi } from '@/test/render-with-providers';

function renderShell(initialPath: string, bundle: TestBundle = createTestBundle()) {
  useSessionStore.setState({ activeProfileId: bundle.fixtures.meta.currentProfileId });
  const memoryRouter = createMemoryRouter(
    [
      {
        path: '/',
        Component: AppShell,
        children: [{ path: '*', element: <div>page content</div> }],
      },
    ],
    { initialEntries: [initialPath] },
  );
  return renderWithApi(<RouterProvider router={memoryRouter} />, bundle);
}

/**
 * Gate da F4 — o recorte de projeto é global (D4): quem escolhe é o cabeçalho,
 * e as telas que existem para comparar projetos ficam de fora dele.
 */
describe('seleção global de projeto no shell (F4)', () => {
  beforeEach(async () => {
    await i18n.changeLanguage('pt-BR');
    useUiStore.setState({ sidebarCollapsed: false, collapsedNavGroups: {}, mobileNavOpen: false });
    usePresentationStore.setState({ modeByProfile: {} });
    useActiveProjectStore.setState({ selectionsByProfile: {} });
  });

  it('o seletor fica no cabeçalho das telas de projeto único', async () => {
    renderShell('/chat');

    expect(await screen.findByRole('combobox', { name: 'Projeto ativo' })).toHaveDisplayValue(
      'Poseidon Frontend',
    );
  });

  it('trocar de projeto no cabeçalho persiste a escolha do perfil', async () => {
    const user = userEvent.setup();
    const bundle = createTestBundle();
    renderShell('/chat', bundle);

    const select = await screen.findByRole('combobox', { name: 'Projeto ativo' });
    await user.selectOptions(select, 'API de Pagamentos');

    await waitFor(() => expect(select).toHaveDisplayValue('API de Pagamentos'));
    const profileId = bundle.fixtures.meta.currentProfileId;
    expect(
      useActiveProjectStore.getState().selectionsByProfile[profileId]?.projectId,
    ).toBeTruthy();
  });

  it('some nas telas multi-projeto, que ignoram a seleção global', async () => {
    renderShell('/projects');

    await screen.findByText('page content');
    expect(screen.queryByRole('combobox', { name: 'Projeto ativo' })).not.toBeInTheDocument();
  });

  it('some também na Central de Entregas e em Organizações', async () => {
    renderShell('/delivery');
    await screen.findByText('page content');
    expect(screen.queryByRole('combobox', { name: 'Projeto ativo' })).not.toBeInTheDocument();

    renderShell('/organizations');
    expect(screen.queryByRole('combobox', { name: 'Projeto ativo' })).not.toBeInTheDocument();
  });
});
